﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using System.Windows.Forms.PropertyGridInternal;
using VideoDownloader.App.BL.Exceptions;
using VideoDownloader.App.Contract;
using VideoDownloader.App.Model;
using Timer = System.Timers.Timer;

namespace VideoDownloader.App.BL
{
    class PluralsightCourseService : ICourseService, IDisposable
    {
        #region Fields

        private readonly object _syncObj = new object();
        private readonly Timer _timer = new Timer(1000);

        private readonly IConfigProvider _configProvider;
        private readonly ISubtitleService _subtitleService;

        private readonly string _userAgent;
        private CancellationToken _token;
        private IProgress<CourseDownloadingProgressArguments> _courseDownloadingProgress;

        private int _totalCourseDownloadingProgessRatio;
        private int _timeout;
        private IProgress<int> _timeoutProgress;
        private bool _disposed;

        #endregion

        #region Constructors

        public PluralsightCourseService(IConfigProvider configProvider, ISubtitleService subtitleService)
        {
            _configProvider = configProvider;
            _subtitleService = subtitleService;
            _userAgent = _configProvider.UserAgent;
        }

        #endregion

        #region Properties

        public string Cookies { get; set; }

        public Dictionary<string, List<CourseDescription>> CoursesByToolName { get; set; } = new Dictionary<string, List<CourseDescription>>();

        public string CachedProductsJson { get; private set; }

        #endregion

        private int GenerateRandomNumber(int min, int max)
        {
            lock (_syncObj)
            {
                var random = new Random();
                return random.Next(min, max);
            }
        }

        public async Task DownloadAsync(string productId,
          IProgress<CourseDownloadingProgressArguments> downloadingProgress,
          IProgress<int> timeoutProgress,
          CancellationToken token)
        {
            _timeoutProgress = timeoutProgress;
            _courseDownloadingProgress = downloadingProgress;
            _token = token;

            var rpcUri = Properties.Settings.Default.RpcUri;
            RpcData rpcData = await GetDeserialisedRpcData(rpcUri, productId, _token);
            await DownloadCourse(rpcData);
        }

        public async Task<string> GetCourseTableOfContentAsync(string productId, CancellationToken token)
        {
            var rpcUri = Properties.Settings.Default.RpcUri;
            StringBuilder tableOfContent = new StringBuilder();
            RpcData rpcData = await GetDeserialisedRpcData(rpcUri, productId, token);
            foreach (var module in rpcData.Payload.Course.Modules)
            {
                tableOfContent.AppendLine($"{module.Title} {module.FormattedDuration}");
                foreach (var clip in module.Clips)
                {
                    tableOfContent.AppendLine($" {clip.Title}  {clip.FormattedDuration}");
                }
                tableOfContent.AppendLine();
            }
            return tableOfContent.ToString();
        }

        private async Task DownloadCourse(RpcData rpcData)
        {
            string destinationFolder = _configProvider.DownloadsPath;

            var course = rpcData.Payload.Course;
            _timer.Elapsed += OnTimerElapsed;

            var courseDirectory = CreateCourseDirectory(GetBaseCourseDirectoryName(destinationFolder, course.Title));
            try
            {
                var moduleCounter = 0;
                foreach (var module in course.Modules)
                {
                    ++moduleCounter;
                    await DownloadModule(rpcData, courseDirectory, moduleCounter, module);
                }
            }
            finally
            {
                var progressArgs = new CourseDownloadingProgressArguments
                {
                    ClipName = string.Empty,
                    CourseProgress = 0,
                    ClipProgress = 0
                };
                _timer.Elapsed -= OnTimerElapsed;
                _courseDownloadingProgress.Report(progressArgs);
                if (_timer != null)
                {
                    _timer.Enabled = false;
                }
                _timeoutProgress.Report(0);
            }
        }

        private void OnTimerElapsed(object sender, ElapsedEventArgs e)
        {
            if (_timeout > 0)
            {
                _timeoutProgress.Report(--_timeout);
            }
        }

        private async Task DownloadModule(RpcData rpcData,
            string courseDirectory,
            int moduleCounter, Module module)
        {
            var moduleDirectory = CreateModuleDirectory(courseDirectory, moduleCounter, module.Title);
            var course = rpcData.Payload.Course;
            var clipCounter = 0;
            string referrer =
                   $"https://{Properties.Settings.Default.SiteHostName}/player?course={course.Name}&author={module.Author}&name={module.Name}&clip={clipCounter - 1}&mode=live";

            HttpHelper httpHelper = new HttpHelper
            {
                AcceptEncoding = string.Empty,
                AcceptHeader = AcceptHeader.All,
                ContentType = ContentType.AppJsonUtf8,
                Cookies = Cookies,
                Referrer = new Uri(referrer),
                UserAgent = _userAgent
            };

            foreach (var clip in module.Clips)
            {
                ++clipCounter;
                var postJson = BuildViewclipPostDataJson(rpcData, moduleCounter, clipCounter);

                var fileName = GetFullFileNameWithoutExtension(clipCounter, moduleDirectory, clip);

                if (!File.Exists($"{fileName}.{Properties.Settings.Default.ClipExtensionMp4}"))
                {
                    ResponseEx viewclipResonse = await httpHelper.SendRequest(HttpMethod.Post,
                        new Uri(Properties.Settings.Default.ViewClipUrl),
                        postJson,
                        _token);

                    if (viewclipResonse.Content == "Unauthorized")
                    {
                        throw new UnauthorizedException(Properties.Resources.CheckYourSubscription);
                    }

                    var clipFile = Newtonsoft.Json.JsonConvert.DeserializeObject<ClipFile>(viewclipResonse.Content);

                    if (rpcData.Payload.Course.CourseHasCaptions)
                    {
                        
                        string unformattedSubtitlesJson = await _subtitleService.DownloadAsync(httpHelper, module.Author, clipCounter - 1, module.Name, _token);
                        Caption[] unformattedSubtitles = Newtonsoft.Json.JsonConvert.DeserializeObject<Caption[]>(unformattedSubtitlesJson);
                        IList<SrtRecord> formattedSubtitles = GetFormattedSubtitles(unformattedSubtitles, clip.Duration);
                        _subtitleService.Write($"{fileName}.{Properties.Settings.Default.SubtitilesExtensionMp4}", formattedSubtitles);
                    }

                    await DownloadClip(new Uri(clipFile.Urls[1].Url),
                      fileName,
                      clipCounter,
                      rpcData.Payload.Course.Modules.Sum(m => m.Clips.Length));
                }
            }
        }

        private List<SrtRecord> GetFormattedSubtitles(Caption[] captions, int totalDuration)
        {
            List<SrtRecord> srtRecords = new List<SrtRecord>();
            System.Globalization.CultureInfo culture = new System.Globalization.CultureInfo("us");

            for (int i = 0; i < captions.Count() - 1; ++i)
            {
                SrtRecord srtRecord = new SrtRecord
                {
                    FromTimeSpan = TimeSpan.FromSeconds(double.Parse(captions[i].DisplayTimeOffset, culture)),
                    ToTimeSpan = TimeSpan.FromSeconds(double.Parse(captions[i + 1].DisplayTimeOffset, culture)),
                    Text = (captions[i].Text)
                };
                srtRecords.Add(srtRecord);
            }

            SrtRecord finalSrtRecord = new SrtRecord
            {
                FromTimeSpan = TimeSpan.FromSeconds(Double.Parse(captions.Last().DisplayTimeOffset, culture)),
                ToTimeSpan = TimeSpan.FromSeconds(Convert.ToDouble(totalDuration)),
                Text = captions.Last().Text
            };

            srtRecords.Add(finalSrtRecord);
            return srtRecords;
        }

        private async Task DownloadClip(Uri clipUrl, string fileNameWithoutExtension, int clipCounter, int partsNumber)
        {
            _token.ThrowIfCancellationRequested();
            Progress<FileDownloadingProgressArguments> fileDownloadingProgress = null;
            try
            {
                RemovePartiallyDownloadedFile(fileNameWithoutExtension);

                var httpHelper = new HttpHelper
                {
                    AcceptEncoding = string.Empty,
                    AcceptHeader = AcceptHeader.All,
                    ContentType = ContentType.Video,
                    Cookies = Cookies,
                    Referrer = new Uri(Properties.Settings.Default.ReferrerUrlForDownloading),
                    UserAgent = _userAgent
                };
                await httpHelper.SendRequest(HttpMethod.Head, clipUrl, null, _token);

                _totalCourseDownloadingProgessRatio = (int)(((double)clipCounter) / partsNumber * 100);
                _courseDownloadingProgress.Report(new CourseDownloadingProgressArguments
                {
                    CurrentAction = Properties.Resources.Downloading,
                    ClipName = $"{fileNameWithoutExtension}.{Properties.Settings.Default.ClipExtensionPart}",
                    CourseProgress = _totalCourseDownloadingProgessRatio,
                    ClipProgress = 0
                });

                fileDownloadingProgress = new Progress<FileDownloadingProgressArguments>();
                fileDownloadingProgress.ProgressChanged += OnProgressChanged;

                await httpHelper.DownloadWithProgressAsync(clipUrl,
                    $"{fileNameWithoutExtension}.{Properties.Settings.Default.ClipExtensionMp4}",
                    fileDownloadingProgress, _token);

                _courseDownloadingProgress.Report(new CourseDownloadingProgressArguments
                {
                    CurrentAction = Properties.Resources.Downloaded,
                    ClipName = $"{fileNameWithoutExtension}.{Properties.Settings.Default.ClipExtensionMp4}",
                    CourseProgress = _totalCourseDownloadingProgessRatio,
                    ClipProgress = 0
                });

                _timer.Enabled = true;
                _timeout = GenerateRandomNumber(_configProvider.MinTimeout, _configProvider.MaxTimeout);
                await Task.Delay(_timeout * 1000, _token);
                _timer.Enabled = false;
            }

            finally
            {
                _timeoutProgress.Report(0);
                if (fileDownloadingProgress != null)
                {
                    fileDownloadingProgress.ProgressChanged -= OnProgressChanged;
                }
            }
        }

        private void OnProgressChanged(object sender, FileDownloadingProgressArguments e)
        {
            var progressArgs = new CourseDownloadingProgressArguments
            {
                CurrentAction = Properties.Resources.Downloading,
                ClipName = e.FileName,
                CourseProgress = _totalCourseDownloadingProgessRatio,
                ClipProgress = e.Percentage
            };
            _courseDownloadingProgress.Report(progressArgs);
        }


        private static void RemovePartiallyDownloadedFile(string fileNameWithoutExtension)
        {
            File.Delete($"{fileNameWithoutExtension}.{Properties.Settings.Default.ClipExtensionPart}");
        }

        private string GetFullFileNameWithoutExtension(int clipCounter, string moduleDirectory, Clip clip)
        {
            return $@"{moduleDirectory}\{clipCounter:00}.{Utils.GetValidPath(clip.Title)}";
        }

        private string CreateModuleDirectory(string courseDirectory, int moduleCounter, string moduleTitle)
        {
            var moduleDirectory = $@"{courseDirectory}\{moduleCounter:00}.{Utils.GetValidPath(moduleTitle)}";
            return Directory.CreateDirectory(moduleDirectory).FullName;
        }

        private string CreateCourseDirectory(string destinationFolder)
        {
            return Directory.CreateDirectory(destinationFolder).FullName;
        }

        private string BuildViewclipPostDataJson(RpcData rpcData, int moduleCounter, int clipCounter)
        {
            Module module = rpcData.Payload.Course.Modules[moduleCounter - 1];
            ViewclipPostData viewclipData = new ViewclipPostData()
            {
                Author = module.Author,
                IncludeCaptions = rpcData.Payload.Course.CourseHasCaptions,
                ClipIndex = clipCounter - 1,
                CourseName = rpcData.Payload.Course.Name,
                Locale = Properties.Settings.Default.EnglishLocale,
                ModuleName = module.Name,
                MediaType = Properties.Settings.Default.ClipExtensionMp4,
                Quality = rpcData.Payload.Course.SupportsWideScreenVideoFormats ? Properties.Settings.Default.Resolution1280x720 : Properties.Settings.Default.Resolution1024x768
            };
            return Newtonsoft.Json.JsonConvert.SerializeObject(viewclipData);
        }

        private async Task<RpcData> GetDeserialisedRpcData(string rpcUri, string productId, CancellationToken token)
        {
            string rpcJson = $"{{\"fn\":\"bootstrapPlayer\", \"payload\":{{\"courseId\":\"{productId}\"}} }}";
            var httpHelper = new HttpHelper
            {
                AcceptHeader = AcceptHeader.JsonTextPlain,
                AcceptEncoding = string.Empty,
                ContentType = ContentType.AppJsonUtf8,
                Cookies = Cookies,
                Referrer = new Uri($"https://{Properties.Settings.Default.SiteHostName}"),
                UserAgent = _userAgent
            };
            var courseRespone = await httpHelper.SendRequest(HttpMethod.Post, new Uri(rpcUri), rpcJson, token);

            return Newtonsoft.Json.JsonConvert.DeserializeObject<RpcData>(courseRespone.Content);
        }

        public string GetBaseCourseDirectoryName(string destinationDirectory, string courseName)
        {
            return $"{destinationDirectory}\\Pluralsight - {Utils.GetValidPath(courseName)}";
        }

        public async Task<bool> ProcessNoncachedProductsJsonAsync()
        {
            try
            {
                CachedProductsJson = await DownloadProductsJsonAsync();
                ProcessResult();
                return !string.IsNullOrEmpty(CachedProductsJson);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private async Task<string> DownloadProductsJsonAsync()
        {
            var httpHelper = new HttpHelper
            {
                AcceptEncoding = string.Empty,
                AcceptHeader = AcceptHeader.HtmlXml,
                ContentType = ContentType.AppXWwwFormUrlencode,
                Cookies = Cookies,
                Referrer = new Uri($"https://{Properties.Settings.Default.SiteHostName}"),
                UserAgent = _userAgent
            };
            var productsJsonResponse = await httpHelper.SendRequest(HttpMethod.Get,
                new Uri(Properties.Settings.Default.AllCoursesUrl),
                null, _token);

            return productsJsonResponse.Content;
        }


        private void ProcessResult()
        {
            CoursesByToolName.Clear();
            var allProducts = Newtonsoft.Json.JsonConvert.DeserializeObject<AllProducts>(CachedProductsJson);
            foreach (var product in allProducts.ResultSets[0].Results)
            {
                try
                {
                    var tools = product.Tools?.Split('|') ?? new[] { "-" };
                    foreach (var tool in tools)
                    {
                        if (!CoursesByToolName.ContainsKey(tool))
                        {
                            CoursesByToolName[tool] = new List<CourseDescription>();
                        }
                        CoursesByToolName[tool].Add(product);
                    }
                }
                catch (Exception)
                {
                    return;
                }
            }
        }

        public async Task<bool> ProcessCachedProductsAsync()
        {
            try
            {
                if (File.Exists(Properties.Settings.Default.FileNameForJsonOfCourses))
                {
                    CachedProductsJson = await Task.Run(() => File.ReadAllText(Properties.Settings.Default.FileNameForJsonOfCourses), _token);
                    ProcessResult();
                }
                else
                {
                    await ProcessNoncachedProductsJsonAsync();
                }

                return !string.IsNullOrEmpty(CachedProductsJson);
            }
            catch (Exception)
            {
                return false;
            }
        }

        public async Task<List<CourseDescription>> GetToolCourses(string toolName)
        {
            var courses = await Task.Run(() => CoursesByToolName.Single(kvp => kvp.Key == toolName).Value, _token);
            return courses;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _timer?.Dispose();

                }
            }
            _disposed = true;
        }
    }
}
