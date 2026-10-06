using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace SailwindCoop.DevConsole
{
    /// <summary>
    /// Loopback-only HTTP front end: <c>GET /status</c>, <c>POST /run</c> (body = C#), <c>POST /reset</c>.
    ///
    /// This executes arbitrary code, so three things keep other software on the machine out of it:
    /// the listener binds 127.0.0.1 only; the Host header must name the loopback (DNS rebinding);
    /// and every POST must carry the <see cref="Header"/> header and no Origin — a browser page cannot
    /// add a custom header without a CORS preflight, which is never answered.
    /// </summary>
    internal sealed class ConsoleServer : IDisposable
    {
        public const string Header = "X-DevConsole";
        private const int PortAttempts = 10;
        private const int MaxBody = 1024 * 1024;

        private readonly ScriptHost _scripts;
        private readonly MainThreadQueue _mainThread;
        private readonly Action<string> _log;
        private readonly int _timeoutMs;
        private HttpListener _listener;
        private Thread _thread;
        private volatile bool _running;

        public int Port { get; private set; }

        public ConsoleServer(ScriptHost scripts, MainThreadQueue mainThread, Action<string> log, int timeoutMs)
        {
            _scripts = scripts; _mainThread = mainThread; _log = log; _timeoutMs = timeoutMs;
        }

        /// <summary>Binds the first free port from <paramref name="firstPort"/> up, so a second copy of
        /// the game on the same machine gets the next one.</summary>
        public bool Start(int firstPort)
        {
            for (int port = firstPort; port < firstPort + PortAttempts; port++)
            {
                var listener = new HttpListener();
                listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
                try { listener.Start(); }
                catch (Exception) { try { listener.Close(); } catch (Exception) { } continue; }
                _listener = listener;
                Port = port;
                _running = true;
                _thread = new Thread(Listen) { IsBackground = true, Name = "SailwindCoopDevConsole" };
                _thread.Start();
                return true;
            }
            return false;
        }

        public void Dispose()
        {
            _running = false;
            try { _listener?.Close(); } catch (Exception) { }
        }

        private void Listen()
        {
            while (_running)
            {
                HttpListenerContext context;
                try { context = _listener.GetContext(); }
                catch (Exception) { if (!_running) return; continue; }
                try { Handle(context); }
                catch (Exception e) { _log("request failed: " + e.Message); }
                finally { try { context.Response.Close(); } catch (Exception) { } }
            }
        }

        private void Handle(HttpListenerContext context)
        {
            var request = context.Request;
            string host = request.Headers["Host"] ?? "";
            if (host != "127.0.0.1:" + Port && host != "localhost:" + Port)
            { Send(context, 403, Error("Host must be 127.0.0.1:" + Port)); return; }

            string path = request.Url.AbsolutePath;
            if (request.HttpMethod == "GET" && path == "/status")
            {
                Send(context, 200, "{\"ok\":true,\"port\":" + Port + ",\"pid\":" + Process.GetCurrentProcess().Id + "}");
                return;
            }
            if (request.HttpMethod != "POST" || (path != "/run" && path != "/reset"))
            { Send(context, 404, Error("Use GET /status, POST /run or POST /reset")); return; }
            if (request.Headers[Header] == null || request.Headers["Origin"] != null)
            { Send(context, 403, Error("POST needs the " + Header + " header and no Origin")); return; }

            if (path == "/reset")
            {
                bool reset = _mainThread.Run(() => _scripts.Reset(), _timeoutMs);
                Send(context, reset ? 200 : 504, reset ? "{\"ok\":true}" : Error("Main thread did not respond"));
                return;
            }

            string code;
            if (request.ContentLength64 > MaxBody) { Send(context, 413, Error("Body is too large")); return; }
            using (var reader = new StreamReader(request.InputStream, Encoding.UTF8)) code = reader.ReadToEnd();
            if (code.Trim().Length == 0) { Send(context, 400, Error("Empty body: send C# code")); return; }

            // Serialization also runs on the main thread: it reads live engine objects.
            string body = null;
            bool ran = _mainThread.Run(() =>
            {
                var result = _scripts.Run(code);
                var sb = new StringBuilder("{\"ok\":").Append(result.Ok ? "true" : "false");
                if (result.Ok && result.HasValue)
                {
                    sb.Append(",\"type\":").Append(Json.Quote(result.Value != null ? result.Value.GetType().FullName : "null"));
                    sb.Append(",\"value\":").Append(Json.Value(result.Value));
                }
                if (!result.Ok) sb.Append(",\"error\":").Append(Json.Quote(result.Error));
                if (!string.IsNullOrEmpty(result.Output)) sb.Append(",\"out\":").Append(Json.Quote(result.Output));
                body = sb.Append('}').ToString();
            }, _timeoutMs);
            Send(context, ran ? 200 : 504, ran ? body : Error("Main thread did not respond in " + _timeoutMs + " ms"));
        }

        private static string Error(string message) => "{\"ok\":false,\"error\":" + Json.Quote(message) + "}";

        private static void Send(HttpListenerContext context, int status, string json)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.ContentLength64 = bytes.Length;
            context.Response.OutputStream.Write(bytes, 0, bytes.Length);
        }
    }
}
