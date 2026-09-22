using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using IntegratedContro.Core;

namespace IntegratedContro.ControlHost;

internal static class MediaSetupProbe
{
    public static async Task VerifyAsync(string executable, string configPath, MediaConfiguration configuration, MediaCredentials credentials)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(configPath)!,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(configPath);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("영상 서버 연결 확인을 시작하지 못했습니다.");
        // Drain without retaining third-party output or exposing config/credentials in an error.
        process.OutputDataReceived += (_, _) => { }; process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine(); process.BeginErrorReadLine();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false })
        { Timeout = TimeSpan.FromSeconds(8) };
        var token = deadline.Token;
        var stage = "API 시작";
        var api = configuration.ApiEndpoint;
        var path = "ic-setup-" + Guid.NewGuid().ToString("N");
        async Task<HttpResponseMessage> Send(HttpMethod method, string url, string? user = null, string? password = null, object? body = null)
        {
            using var request = new HttpRequestMessage(method, url);
            if (user is not null)
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password)));
            if (body is not null) request.Content = JsonContent.Create(body);
            return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        }
        try
        {
            while (true)
            {
                if (process.HasExited) throw new InvalidOperationException("영상 서버가 연결 확인 중 종료됐습니다. 실행 파일·설정·포트를 확인하세요.");
                try
                {
                    using var ready = await Send(HttpMethod.Get, api + "/v3/paths/list", configuration.ApiUser, credentials.ApiPassword);
                    if (ready.StatusCode != HttpStatusCode.OK)
                        throw new InvalidOperationException("영상 서버 API 인증을 확인하지 못했습니다.");
                    await ready.Content.LoadIntoBufferAsync(65536, token);
                    using var content = JsonDocument.Parse(await ready.Content.ReadAsByteArrayAsync(token));
                    if (!content.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                        throw new InvalidOperationException("MediaMTX API 응답 형식을 확인하세요.");
                    break;
                }
                catch (HttpRequestException) { await Task.Delay(100, token); }
            }
            stage = "API 익명 접근 차단";
            using (var denied = await Send(HttpMethod.Get, api + "/v3/paths/list"))
                if (denied.StatusCode != HttpStatusCode.Unauthorized)
                    throw new InvalidOperationException("영상 서버 API의 익명 접근 차단을 확인하지 못했습니다.");
            stage = "API·HLS 권한 분리";
            using (var denied = await Send(HttpMethod.Get, api + "/v3/paths/list", configuration.HlsUser, credentials.HlsPassword))
                if (denied.StatusCode != HttpStatusCode.Unauthorized)
                    throw new InvalidOperationException("영상 서버 API·HLS 계정 권한 분리를 확인하지 못했습니다.");
            stage = "API 경로 등록";
            using (var added = await Send(HttpMethod.Post, api + "/v3/config/paths/add/" + path,
                       configuration.ApiUser, credentials.ApiPassword, new { source = "publisher" }))
                if (!added.IsSuccessStatusCode) throw new InvalidOperationException("영상 서버 API의 경로 등록 권한을 확인하지 못했습니다.");
            // The HLS index authenticates read permission without requiring a camera stream.
            stage = "HLS 읽기 인증";
            var url = configuration.HlsEndpoint + "/" + path + "/";
            using (var hls = await Send(HttpMethod.Get, url, configuration.HlsUser, credentials.HlsPassword))
                if (hls.StatusCode != HttpStatusCode.OK || hls.Content.Headers.ContentType?.MediaType != "text/html")
                    throw new InvalidOperationException("영상 서버 HLS 읽기 인증을 확인하지 못했습니다.");
            stage = "HLS 익명 접근 차단";
            using (var denied = await Send(HttpMethod.Get, url))
                if (denied.StatusCode != HttpStatusCode.Unauthorized)
                    throw new InvalidOperationException("영상 서버 HLS의 익명 접근 차단을 확인하지 못했습니다.");
            stage = "API 확인 경로 정리";
            using (var removed = await Send(HttpMethod.Delete, api + "/v3/config/paths/delete/" + path,
                       configuration.ApiUser, credentials.ApiPassword))
                if (!removed.IsSuccessStatusCode) throw new InvalidOperationException("영상 서버 확인 경로를 정리하지 못했습니다.");
            if (process.HasExited) throw new InvalidOperationException("영상 서버가 연결 확인 중 종료됐습니다.");
        }
        catch (OperationCanceledException) { throw new InvalidOperationException($"영상 서버 {stage} 확인 시간이 초과됐습니다. 같은 설정으로 다시 확인하세요."); }
        catch (HttpRequestException) { throw new InvalidOperationException("영상 서버 API/HLS에 연결하지 못했습니다. 포트와 실행 파일을 확인하세요."); }
        finally
        {
            // Only this temporary child is ours. Its API paths are in memory and disappear on exit.
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
