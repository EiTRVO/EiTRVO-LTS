using System.Net;
using EiTRVO.Core.Auth.Tests.Fakes;

namespace EiTRVO.Core.Auth.Tests;

/// <summary>
/// M6-d：认证侧的 HTTP 桩同样必须能承受并发。
/// </summary>
/// <remarks>
/// 与 Launcher 侧同一个起因（那边是 <c>ManyFilesAreDownloadedWithBoundedConcurrency</c> 偶发丢请求）：
/// 桩的 <c>List&lt;T&gt;.Add</c> 在多线程下会丢条目，于是"发了几次请求"的断言随机失败、
/// 报出来的现象却像产品代码的问题。皮肤接口的 401 → 刷新 → 重试这条路就会并发发请求。
/// </remarks>
[TestClass]
public sealed class StubHttpTests
{
    [TestMethod]
    public async Task ConcurrentRequestsAreAllRecorded()
    {
        const int PerRound = 64;
        const int Rounds = 8;

        var handler = new StubHandler().RespondTo("minecraft/profile", HttpStatusCode.OK, "{}");
        using var client = new HttpClient(handler, disposeHandler: false);

        for (var round = 1; round <= Rounds; round++)
        {
            await Task.WhenAll(Enumerable.Range(0, PerRound).Select(_ => Task.Run(async () =>
            {
                using var response = await client
                    .GetAsync("https://api.minecraftservices.com/minecraft/profile")
                    .ConfigureAwait(false);

                return response.StatusCode;
            })));

            Assert.AreEqual(
                PerRound * round,
                handler.Requests.Count,
                $"并发下每一次请求都必须被记下来（第 {round} 轮就对不上了）。");
        }
    }
}
