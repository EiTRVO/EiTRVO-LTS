using System.Net;
using EiTRVO.Core.Launcher.Tests.Fakes;

namespace EiTRVO.Core.Launcher.Tests;

/// <summary>
/// M6-d：HTTP 桩自己必须能承受并发。
/// </summary>
/// <remarks>
/// 起因是一个**偶发**失败：<c>ManyFilesAreDownloadedWithBoundedConcurrency</c> 偶尔报"只发了 7 次请求"
/// （8 个文件、并发 4）。查下来是桩的 <c>List&lt;T&gt;.Add</c> 在多线程下丢条目 —— 也就是
/// **检查工具自己有 bug**，而它报出来的现象却像"下载器少发了请求"。
/// <para>
/// 漏检/误报的检查比没有检查更糟（AGENTS.md §5.3 的同一条道理），所以这里直接钉住桩本身：
/// N 个线程同时发请求，记录一条都不能少。
/// </para>
/// </remarks>
[TestClass]
public sealed class StubHttpTests
{
    [TestMethod]
    public async Task ConcurrentRequestsAreAllRecorded()
    {
        // 每轮 64 个请求、跑 8 轮：`Task.Run` 才让 SendAsync 真的在不同线程上同时执行
        // （直接 Task.WhenAll(client.GetAsync(...)) 会先在调用线程上同步跑完每个同步处理器，
        // 那样这条用例根本碰不到并发路径）。
        const int PerRound = 64;
        const int Rounds = 8;

        var handler = new StubHandler().RespondTo("objects/", HttpStatusCode.OK, "body");
        using var client = new HttpClient(handler, disposeHandler: false);

        for (var round = 1; round <= Rounds; round++)
        {
            await Task.WhenAll(Enumerable.Range(0, PerRound).Select(index => Task.Run(async () =>
            {
                using var response = await client
                    .GetAsync($"https://piston-data.mojang.com/v1/objects/{index}/client.jar")
                    .ConfigureAwait(false);

                return response.StatusCode;
            })));

            Assert.AreEqual(
                PerRound * round,
                handler.Requests.Count,
                $"并发下每一次请求都必须被记下来，否则「发了几次」的断言会随机翻车（第 {round} 轮就对不上了）。");
        }

        Assert.AreEqual(
            PerRound,
            handler.Requests.Select(static request => request.RequestUri!.ToString()).Distinct().Count(),
            "每个地址都被请求了 Rounds 次，地址集合本身不该串。");
    }

    [TestMethod]
    public async Task ConcurrentClientCreationIsAllRecorded()
    {
        const int Count = 64;

        var handler = new StubHandler().RespondTo("objects/", HttpStatusCode.OK, "body");
        var factory = new FakeHttpClientFactory(handler);

        await Task.WhenAll(Enumerable.Range(0, Count).Select(_ => Task.Run(() =>
        {
            using var client = factory.CreateClient("core");
            return client.BaseAddress;
        })));

        Assert.AreEqual(Count, factory.RequestedNames.Count, "「用的是哪个命名客户端」这条断言同样不能被并发吃掉记录。");
    }
}
