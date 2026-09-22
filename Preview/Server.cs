using System;
using System.IO;
using System.Net;
using System.Text;

namespace Preview
{
    /// <summary>
    /// Serves the generated pages over localhost so they can be opened in a normal browser.
    ///
    /// Opening the files directly with file:// mostly works, but a served page behaves the way the
    /// real thing does (proper origin, no local-file restrictions on image loading), and a fixed
    /// URL means you can just hit refresh instead of re-finding the file each time.
    ///
    /// Every request REGENERATES the pages, so editing fixture.json and refreshing is enough to see
    /// the change - only a C# edit needs a rebuild.
    ///
    /// Uses HttpListener, which is in the framework and needs no admin rights for a loopback-only
    /// prefix.
    /// </summary>
    internal static class Server
    {
        public static void Run(int port, Action regenerate)
        {
            var listener = new HttpListener();

            // HttpListener matches on the HOST HEADER, so registering only "localhost" makes
            // http://127.0.0.1:port 400 out and look like the server is down. Register both.
            foreach (var host in new[] { "localhost", "127.0.0.1" })
                listener.Prefixes.Add("http://" + host + ":" + port + "/");

            try
            {
                listener.Start();
            }
            catch (HttpListenerException e)
            {
                Console.WriteLine("could not bind port " + port + ": " + e.Message);
                Console.WriteLine("try another port:  Preview.exe --serve 8081");
                return;
            }

            Console.WriteLine();
            Console.WriteLine("  preview server running - press Ctrl+C to stop");
            Console.WriteLine("    board    http://localhost:" + port + "/board");
            Console.WriteLine("    panels   http://localhost:" + port + "/preview");
            Console.WriteLine();
            Console.WriteLine("  each request regenerates; refresh after editing fixture.json");
            Console.WriteLine();

            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = listener.GetContext(); }
                catch { break; }

                try { Serve(ctx, regenerate); }
                catch (Exception e) { Console.WriteLine("request failed: " + e.Message); }
            }
        }

        private static void Serve(HttpListenerContext ctx, Action regenerate)
        {
            var path = (ctx.Request.Url.AbsolutePath ?? "/").TrimEnd('/');
            if (path == "/favicon.ico") { ctx.Response.StatusCode = 404; ctx.Response.Close(); return; }

            regenerate();

            string body;
            switch (path)
            {
                case "":
                case "/index.html":
                    body = Index();
                    break;
                case "/board":
                case "/board.html":
                    body = Read("board.html");
                    break;
                case "/preview":
                case "/preview.html":
                    body = Read("preview.html");
                    break;
                default:
                    ctx.Response.StatusCode = 404;
                    body = "<h1>404</h1><p><a href='/'>index</a></p>";
                    break;
            }

            var bytes = Encoding.UTF8.GetBytes(body);
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.ContentLength64 = bytes.Length;
            // These pages are regenerated per request; never let the browser cache them.
            ctx.Response.Headers["Cache-Control"] = "no-store";
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.Close();

            Console.WriteLine("  " + ctx.Response.StatusCode + "  " + (path == "" ? "/" : path));
        }

        private static string Read(string file)
        {
            var p = Path.GetFullPath(file);
            return File.Exists(p) ? File.ReadAllText(p) : "<h1>not generated</h1>";
        }

        private static string Index()
        {
            return @"<!doctype html><meta charset='utf-8'><title>Prize Tracker preview</title>
<style>body{background:#14161b;color:#e9edf3;font:15px/1.6 'Segoe UI',sans-serif;padding:40px}
a{color:#66b0ff;display:block;margin:10px 0;font-size:17px}
p{color:#79808f;max-width:640px}</style>
<h1>Prize Tracker preview</h1>
<a href='/board'>Board @ 1920&times;1080 with the overlay on top</a>
<a href='/preview'>Overlay panels across four scenarios</a>
<p>Both are rendered from the real Tracker. Each request regenerates them, so refresh after
editing <code>Preview/fixture.json</code>; a C# change needs a rebuild.</p>";
        }
    }
}
