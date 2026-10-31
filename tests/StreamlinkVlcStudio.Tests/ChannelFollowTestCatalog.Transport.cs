using System.Net.Sockets;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using StreamlinkVlcStudio.App.Wpf.Follows;

internal static partial class ChannelFollowTestCatalog
{
    private static Task BrowserTransportBodyAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        using var portProbe = new TcpListener(IPAddress.Loopback, 0);
        portProbe.Start();
        var address = $"http://127.0.0.1:{((IPEndPoint)portProbe.LocalEndpoint).Port}/";
        portProbe.Stop();
        using var server = new HttpListener();
        server.Prefixes.Add(address);
        server.Start();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = cancellation.Token;
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var responseBody = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestBody = "";
        var expected = MutationRequest();
        var serverTask = ServeAsync();
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder:
            Path.Combine(Path.GetTempPath(), $"channel-follow-transport-{Guid.NewGuid():N}"));
        var host = new HwndSource(new HwndSourceParameters("Follow request transport test")
        {
            Width = 1280,
            Height = 800,
            WindowStyle = 0
        });
        var controller = await WebView2ControllerLifetime.CreateAsync(host,
            environment.CreateCoreWebView2ControllerAsync, TimeSpan.FromSeconds(15), token);
        try
        {
            controller.Bounds = new System.Drawing.Rectangle(0, 0, 1280, 800);
            controller.IsVisible = true;
            var core = controller.CoreWebView2;
            core.AddWebResourceRequestedFilter(address + "gql", CoreWebView2WebResourceContext.All,
                CoreWebView2WebResourceRequestSourceKinds.All);
            core.WebResourceRequested += (_, args) =>
                requestBody = ChannelFollowBrowserClient.ReadRequestBody(args.Request, preserveForSend: true);
            core.WebResourceResponseReceived += (_, args) =>
            {
                if (args.Request.Uri == address + "gql")
                    responseBody.TrySetResult(ChannelFollowBrowserClient.ReadRequestBody(args.Request));
            };
            core.Navigate(address);
            Assert.Equal(expected, await received.Task.WaitAsync(token));
            Assert.Equal(expected, await responseBody.Task.WaitAsync(token));
            Assert.Equal(expected, requestBody);
        }
        finally
        {
            controller.Close();
            host.Dispose();
            cancellation.Cancel();
            server.Close();
            await serverTask;
        }

        async Task ServeAsync()
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var context = await server.GetContextAsync().WaitAsync(token);
                    var responseWritten = false;
                    try
                    {
                        var post = context.Request.HttpMethod == "POST";
                        if (post)
                        {
                            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                            received.TrySetResult(await reader.ReadToEndAsync(token));
                        }
                        var content = post ? "{}" : $$"""
                            <!doctype html><script>
                            fetch('/gql', { method: 'POST', headers: {'Content-Type':'application/json'},
                                body: {{JsonSerializer.Serialize(expected)}} });
                            </script>
                            """;
                        var bytes = Encoding.UTF8.GetBytes(content);
                        context.Response.ContentType = post ? "application/json" : "text/html";
                        context.Response.ContentLength64 = bytes.Length;
                        await context.Response.OutputStream.WriteAsync(bytes, token);
                        responseWritten = true;
                    }
                    finally
                    {
                        if (responseWritten) context.Response.Close();
                        else context.Response.Abort();
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (HttpListenerException) when (token.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (token.IsCancellationRequested) { }
        }
    });
}
