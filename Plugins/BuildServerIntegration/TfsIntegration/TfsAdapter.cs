using System;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using GitCommands.Utils;
using GitUIPluginInterfaces;
using GitUIPluginInterfaces.BuildServerIntegration;
using TfsInterop.Interface;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Net.Http;
using System.Linq;
using System.IO;
using Newtonsoft.Json.Linq;
using System.Net;

namespace TfsIntegration
{

    [MetadataAttribute]
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public class TfsIntegrationMetadata : BuildServerAdapterMetadataAttribute
    {
        public TfsIntegrationMetadata(string buildServerType)
            : base(buildServerType)
        {
        }

        public override string CanBeLoaded
        {
            get
            {
                if (EnvUtils.IsNet4FullOrHigher())
                    return null;
                return ".Net 4 full framework required";
            }
        }
    }

    [Export(typeof(IBuildServerAdapter))]
    [TfsIntegrationMetadata("Team Foundation Server")]
    [PartCreationPolicy(CreationPolicy.NonShared)]
    internal class TfsAdapter : IBuildServerAdapter
    {
        private IBuildServerWatcher _buildServerWatcher;
        string _tfsServer;
        private IList<Task<IEnumerable<string>>> _getBuildUrls;
        string _tfsTeamCollectionName;
        string _projectName;
        Regex _tfsBuildDefinitionNameFilter;
        private HttpClient _httpClient;

        public void Initialize(IBuildServerWatcher buildServerWatcher, ISettingsSource config, Func<string, bool> isCommitInRevisionGrid)
        {
            if (_buildServerWatcher != null)
                throw new InvalidOperationException("Already initialized");

            _buildServerWatcher = buildServerWatcher;

            _tfsServer = config.GetString("TfsServer", null);
            _tfsTeamCollectionName = config.GetString("TfsTeamCollectionName", null);
            _projectName = config.GetString("ProjectName", null);
            var tfsBuildDefinitionNameFilterSetting = config.GetString("TfsBuildDefinitionName", "");
            if (!BuildServerSettingsHelper.IsRegexValid(tfsBuildDefinitionNameFilterSetting))
            {
                return;
            }

            _tfsBuildDefinitionNameFilter = new Regex(tfsBuildDefinitionNameFilterSetting, RegexOptions.Compiled);

            if (!string.IsNullOrEmpty(_tfsServer)
                && !string.IsNullOrEmpty(_tfsTeamCollectionName)
                && !string.IsNullOrEmpty(_projectName))
            {
                var baseAdress = _tfsServer.Contains("://")
                                     ? new Uri($"{_tfsServer}/{_tfsTeamCollectionName}/{_projectName}/_apis/build/builds?definitions=5&status8Filter=completed&top=1&api-version=2.0", UriKind.Absolute)
                                     : new Uri(string.Format("{0}://{1}", Uri.UriSchemeHttp, _tfsServer), UriKind.Absolute);

                _httpClient = new HttpClient(new HttpClientHandler() { UseDefaultCredentials = true });
                _httpClient.Timeout = TimeSpan.FromMinutes(2);
                _httpClient.BaseAddress = baseAdress;

                var buildServerCredentials = buildServerWatcher.GetBuildServerCredentials(this, true);

                _getBuildUrls = new List<Task<IEnumerable<string>>>();

                string[] projectUrls = _projectName.Split(new char[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var projectUrl in projectUrls.Select(s => baseAdress + "job/" + s.Trim() + "/"))
                {
                    AddGetBuildUrl(projectUrl);
                }
            }
        }

        private ITfsHelper LoadAssemblyAndConnectToServer(string assembly)
        {
            try
            {
                Trace.WriteLine("Try loading " + assembly + ".dll ...");
                var loadedAssembly = Assembly.Load(assembly);
                var tfsHelper = loadedAssembly.CreateInstance("TfsInterop.TfsHelper") as ITfsHelper;
                Trace.WriteLine("Create instance... OK");

                if (tfsHelper != null && tfsHelper.IsDependencyOk())
                {
                    tfsHelper.ConnectToTfsServer(_tfsServer, _tfsTeamCollectionName, _projectName, _tfsBuildDefinitionNameFilter);
                    Trace.WriteLine("Connection... OK");
                    return tfsHelper;
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine(ex.Message);
            }
            return null;
        }

        /// <summary>
        /// Gets a unique key which identifies this build server.
        /// </summary>
        public string UniqueKey
        {
            get { return _tfsServer + "/" + _tfsTeamCollectionName + "/" + _projectName; }
        }

        public IObservable<BuildInfo> GetFinishedBuildsSince(IScheduler scheduler, DateTime? sinceDate = null)
        {
            return GetBuilds(scheduler, sinceDate, false);
        }

        public IObservable<BuildInfo> GetRunningBuilds(IScheduler scheduler)
        {
            return GetBuilds(scheduler, null, true);
        }

        public IObservable<BuildInfo> GetBuilds(IScheduler scheduler, DateTime? sinceDate = null, bool? running = null)
        {
            //if (_tfsHelper == null)
            //    return Observable.Empty<BuildInfo>();

            return Observable.Create<BuildInfo>((observer, cancellationToken) =>
                Task<IDisposable>.Factory.StartNew(
                    () => scheduler.Schedule(() => ObserveBuilds(sinceDate, running, observer, cancellationToken))));
        }

        private void ObserveBuilds(DateTime? sinceDate, bool? running, IObserver<BuildInfo> observer, CancellationToken cancellationToken)
        {
            try
            {
                if (_getBuildUrls.All(t => t.IsCanceled))
                {
                    observer.OnCompleted();
                    return;
                }

                foreach (var currentGetBuildUrls in _getBuildUrls)
                {
                    if (currentGetBuildUrls.IsFaulted)
                    {
                        Debug.Assert(currentGetBuildUrls.Exception != null);

                        observer.OnError(currentGetBuildUrls.Exception);
                        continue;
                    }

                    var buildContents = currentGetBuildUrls.Result
                        .Select(buildUrl => GetResponseAsync(FormatToGetJson(buildUrl), cancellationToken).Result)
                        .Where(s => !string.IsNullOrEmpty(s)).ToArray();

                    foreach (var buildDetails in buildContents)
                    {
                        JObject buildDescription = JObject.Parse(buildDetails);
                        var startDate = TimestampToDateTime(buildDescription["startTime"].ToObject<long>());
                        var isRunning = buildDescription["building"].ToObject<bool>();

                        if (sinceDate.HasValue && sinceDate.Value > startDate)
                            continue;

                        if (running.HasValue && running.Value != isRunning)
                            continue;

                        var buildInfo = CreateBuildInfo(buildDescription);
                        if (buildInfo.CommitHashList.Any())
                        {
                            observer.OnNext(buildInfo);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Do nothing, the observer is already stopped
            }
            catch (Exception ex)
            {
                observer.OnError(ex);
            }
        }

        private static BuildInfo CreateBuildInfo(JObject buildDetail)
        {
            return new BuildInfo() { Description = "Success", StartDate = DateTime.Now };
        }

        private void AddGetBuildUrl(string projectUrl)
        {
            _getBuildUrls.Add(GetResponseAsync(FormatToGetJson(projectUrl), CancellationToken.None)
                .ContinueWith(
                    task =>
                    {
                        JObject jobDescription = JObject.Parse(task.Result);
                        return jobDescription["builds"].Select(b => b["url"].ToObject<string>());
                    },
                    TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.AttachedToParent));
        }

        private string FormatToGetJson(string restServicePath)
        {
            if (!restServicePath.EndsWith("/"))
                restServicePath += "/";
            return restServicePath + "api/json";
        }

        private Task<Stream> GetStreamFromHttpResponseAsync(Task<HttpResponseMessage> task, string restServicePath, CancellationToken cancellationToken)
        {
#if !__MonoCS__
            bool retry = task.IsCanceled && !cancellationToken.IsCancellationRequested;
            bool unauthorized = task.Status == TaskStatus.RanToCompletion &&
                                task.Result.StatusCode == HttpStatusCode.Unauthorized;

            if (!retry)
            {
                if (task.Result.IsSuccessStatusCode)
                {
                    var httpContent = task.Result.Content;

                    if (httpContent.Headers.ContentType.MediaType == "text/html")
                    {
                        // Jenkins responds with an HTML login page when guest access is denied.
                        unauthorized = true;
                    }
                    else
                    {
                        return httpContent.ReadAsStreamAsync();
                    }
                }
                else if (task.Result.StatusCode == HttpStatusCode.Forbidden)
                {
                    unauthorized = true;
                }
            }

            if (retry)
            {
                return GetStreamAsync(restServicePath, cancellationToken);
            }

            if (unauthorized)
            {
                var buildServerCredentials = _buildServerWatcher.GetBuildServerCredentials(this, false);

                if (buildServerCredentials != null)
                {

                    return GetStreamAsync(restServicePath, cancellationToken);
                }

                throw new OperationCanceledException(task.Result.ReasonPhrase);
            }

            throw new HttpRequestException(task.Result.ReasonPhrase);
#else
            return null;
#endif
        }

        private Task<string> GetResponseAsync(string relativePath, CancellationToken cancellationToken)
        {
            var getStreamTask = GetStreamAsync(relativePath, cancellationToken);

            return getStreamTask.ContinueWith(
                task =>
                {
                    if (task.Status != TaskStatus.RanToCompletion)
                        return string.Empty;
                    using (var responseStream = task.Result)
                    {
                        return new StreamReader(responseStream).ReadToEnd();
                    }
                },
                cancellationToken,
                TaskContinuationOptions.AttachedToParent,
                TaskScheduler.Current);
        }
        private Task<Stream> GetStreamAsync(string restServicePath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return _httpClient.GetAsync(restServicePath, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                             .ContinueWith(
                                 task => GetStreamFromHttpResponseAsync(task, restServicePath, cancellationToken),
                                 cancellationToken,
                                 TaskContinuationOptions.AttachedToParent,
                                 TaskScheduler.Current)
                             .Unwrap();
        }

        private DateTime TimestampToDateTime(long timestamp)
        {
            return new DateTime(1970, 1, 1, 0, 0, 0, DateTime.Now.Kind).AddMilliseconds(timestamp);
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
        }
    }
}
