using CefSharp;
using CefSharp.OffScreen;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace MediaPlayerCore.Resolvers
{
    public class CefSharpResolverResult
    {
        public string Url { get; set; } = string.Empty;
        public Dictionary<string, string> Headers { get; set; } = new Dictionary<string, string>();
        public string M3u8Content { get; set; } = null;
        public IDisposable BrowserHandle { get; set; } = null;
    }

    public static class CefSharpResolver
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto, SetLastError = true)]
        private static extern bool SetDllDirectory(string lpPathName);

        private static bool _initialized = false;
        private static readonly object _initLock = new object();

        public static void Initialize(string pluginDir)
        {
            lock (_initLock)
            {
                if (_initialized) return;

                string cefDir = Path.Combine(pluginDir, "cef");

                // Disable CefSharp ModuleInitializer which crashes on Path.GetDirectoryName(Assembly.Location)
                // when loaded dynamically by Dalamud (where Location is empty)
                Environment.SetEnvironmentVariable("CefSharpDisableModuleInitializer", "1");
                Environment.SetEnvironmentVariable("CEFSHARP_DISABLE_MODULE_INITIALIZER", "1");

                // Explicitly resolve CefSharp assemblies from the cef/ folder since they are hidden from Dalamud
                AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
                {
                    var name = new AssemblyName(args.Name).Name;
                    if (name.StartsWith("CefSharp"))
                    {
                        string path = Path.Combine(cefDir, name + ".dll");
                        if (File.Exists(path))
                        {
                            return Assembly.LoadFrom(path);
                        }
                    }
                    return null;
                };

                try {
                    SetDllDirectory(cefDir);
                    
                    string coreRuntimePath = Path.Combine(cefDir, "CefSharp.Core.Runtime.dll");
                    if (File.Exists(coreRuntimePath)) {
                        Assembly.LoadFrom(coreRuntimePath);
                    }
                } catch (Exception ex) { 
                    Console.WriteLine("Failed to preload CefSharp.Core.Runtime.dll! " + ex);
                }

                try {
                    InitializeInternal(pluginDir);
                } finally {
                    try { SetDllDirectory(null); } catch { }
                }
            }
        }

        // Use a separate method so the JIT compiler doesn't load CefSharp types until AFTER we've pre-loaded the assembly!
        private static void InitializeInternal(string pluginDir)
        {
            var settings = new CefSettings();
            settings.WindowlessRenderingEnabled = true;
            settings.CachePath = Path.Combine(pluginDir, "cef_cache");
            settings.LogFile = Path.Combine(pluginDir, "cef.log");
            if (!settings.CefCommandLineArgs.ContainsKey("disable-dev-shm-usage")) settings.CefCommandLineArgs.Add("disable-dev-shm-usage", "1");
            if (!settings.CefCommandLineArgs.ContainsKey("mute-audio")) settings.CefCommandLineArgs.Add("mute-audio", "1");
            if (!settings.CefCommandLineArgs.ContainsKey("no-sandbox")) settings.CefCommandLineArgs.Add("no-sandbox", "1");
            if (!settings.CefCommandLineArgs.ContainsKey("disable-web-security")) settings.CefCommandLineArgs.Add("disable-web-security", "1");
            if (!settings.CefCommandLineArgs.ContainsKey("allow-running-insecure-content")) settings.CefCommandLineArgs.Add("allow-running-insecure-content", "1");

            // Spoof standard Chrome User Agent to help bypass Cloudflare / bot protection
            settings.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
            
            string cefDir = Path.Combine(pluginDir, "cef");
            settings.BrowserSubprocessPath = Path.Combine(cefDir, "CefSharp.BrowserSubprocess.exe");
            settings.LocalesDirPath = Path.Combine(cefDir, "locales");
            settings.ResourcesDirPath = cefDir;

            if (CefSharp.Cef.IsInitialized != true)
            {
                if (!CefSharp.Cef.Initialize(settings, performDependencyCheck: false, browserProcessHandler: null))
                {
                    throw new InvalidOperationException("CefSharp failed to initialize! Check cef.log for details, or ensure Visual C++ Redistributable is installed.");
                }
            }
            _initialized = true;
        }

        public static void Shutdown()
        {
            lock (_initLock)
            {
                if (_initialized)
                {
                    Cef.Shutdown();
                    _initialized = false;
                }
            }
        }

        public static async Task<CefSharpResolverResult?> ResolveStreamUrlAsync(string url, int timeoutMs = 20000)
        {
            if (!_initialized)
            {
                throw new InvalidOperationException("CefSharpResolver is not initialized.");
            }

            var tcs = new TaskCompletionSource<CefSharpResolverResult?>();
            
            // We must run browser creation on the thread pool, as it might block or require specific threading
            await Task.Run(async () =>
            {
                ChromiumWebBrowser browser = null;
                try
                {
                    browser = new ChromiumWebBrowser(url);
                    var handler = new StreamInterceptorRequestHandler(tcs);
                    browser.RequestHandler = handler;

                    // Timeout mechanism
                    using var cts = new CancellationTokenSource(timeoutMs);
                    cts.Token.Register(() =>
                    {
                        if (!tcs.Task.IsCompleted)
                        {
                            tcs.TrySetResult(null); // Timeout
                        }
                    });

                    // Wait for the TaskCompletionSource to be set by the RequestHandler
                    var res = await tcs.Task;
                    if (res != null)
                    {
                        res.BrowserHandle = browser;
                        var cookies = await browser.GetCookieManager().VisitUrlCookiesAsync(url, true);
                        var cookieStrs = new List<string>();
                        foreach (var c in cookies)
                        {
                            cookieStrs.Add($"{c.Name}={c.Value}");
                        }
                        if (cookieStrs.Count > 0)
                        {
                            res.Headers["Cookie"] = string.Join("; ", cookieStrs);
                        }
                        
                        if (res.Url.Contains(".m3u8"))
                        {
                            try
                            {
                                var jsResponse = await browser.EvaluateScriptAsync($"fetch('{res.Url}').then(r => r.text())");
                                if (jsResponse.Success && jsResponse.Result is string m3u8Text)
                                {
                                    res.M3u8Content = m3u8Text;
                                    System.Diagnostics.Debug.WriteLine($"[CefSharpResolver] M3U8 Content:\n{m3u8Text}");
                                }
                            }
                            catch { }
                        }
                    }
                    else
                    {
                        browser?.Dispose();
                    }
                }
                catch
                {
                    browser?.Dispose();
                }
            });

            return await tcs.Task;
        }
    }

    public class StreamInterceptorRequestHandler : CefSharp.Handler.RequestHandler
    {
        private readonly TaskCompletionSource<CefSharpResolverResult?> _tcs;

        public StreamInterceptorRequestHandler(TaskCompletionSource<CefSharpResolverResult?> tcs)
        {
            _tcs = tcs;
        }

        protected override IResourceRequestHandler GetResourceRequestHandler(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, IRequest request, bool isNavigation, bool isDownload, string requestInitiator, ref bool disableDefaultHandling)
        {
            return new StreamInterceptorResourceRequestHandler(_tcs);
        }
    }

    public class StreamInterceptorResourceRequestHandler : CefSharp.Handler.ResourceRequestHandler
    {
        private readonly TaskCompletionSource<CefSharpResolverResult?> _tcs;

        public StreamInterceptorResourceRequestHandler(TaskCompletionSource<CefSharpResolverResult?> tcs)
        {
            _tcs = tcs;
        }

        protected override CefReturnValue OnBeforeResourceLoad(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, IRequest request, IRequestCallback callback)
        {
            string url = request.Url;
            
            // Look for m3u8 playlists or raw video files
            if (url.Contains(".m3u8") || url.Contains(".mp4"))
            {
                if (!_tcs.Task.IsCompleted)
                {
                    var result = new CefSharpResolverResult { Url = url };
                    
                    // Extract headers like Referer
                    foreach (string key in request.Headers.AllKeys)
                    {
                        if (key != null)
                            result.Headers[key] = request.Headers[key] ?? "";
                    }

                    if (!result.Headers.ContainsKey("Referer"))
                    {
                        result.Headers["Referer"] = request.ReferrerUrl ?? chromiumWebBrowser.Address ?? "https://nepu.to/";
                    }

                    _tcs.TrySetResult(result);
                }
            }
            return CefReturnValue.Continue;
        }
    }
}
