using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.UI.Dispatching;
using TeaMind.Core;

namespace TeaMind.App;

internal sealed record CanvasWorkSnapshot(long Layouts, double LayoutMs, long Draws, double DrawMs, long CacheBuilds, double CacheBuildMs)
{
    public CanvasWorkSnapshot Since(CanvasWorkSnapshot before) => new(Layouts - before.Layouts, LayoutMs - before.LayoutMs,
        Draws - before.Draws, DrawMs - before.DrawMs, CacheBuilds - before.CacheBuilds, CacheBuildMs - before.CacheBuildMs);
}

public sealed partial class MainWindow
{
    private long refreshCount;

    // A worker thread can observe queue delays while the UI thread is blocked.
    private sealed class DispatcherProbe(DispatcherQueue dispatcher) : IAsyncDisposable
    {
        private readonly CancellationTokenSource cancellation = new();
        private readonly ConcurrentQueue<double> samples = new();
        private int pending;
        private Task? worker;
        public void Start() => worker = Task.Run(async () =>
        {
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    await Task.Delay(8, cancellation.Token);
                    if (Interlocked.Exchange(ref pending, 1) != 0) continue;
                    var start = Stopwatch.GetTimestamp();
                    if (!dispatcher.TryEnqueue(() =>
                    {
                        samples.Enqueue(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                        Interlocked.Exchange(ref pending, 0);
                    })) Interlocked.Exchange(ref pending, 0);
                }
            }
            catch (OperationCanceledException) { }
        });
        public object Result()
        {
            var all = samples.Order().ToArray();
            return new { samples = all.Length, averageMs = all.Length == 0 ? 0 : all.Average(),
                p95Ms = all.Length == 0 ? 0 : all[(int)Math.Ceiling(all.Length * .95) - 1], maxMs = all.Length == 0 ? 0 : all[^1] };
        }
        public async ValueTask DisposeAsync()
        {
            cancellation.Cancel();
            if (worker != null) await worker;
            cancellation.Dispose();
        }
    }

    private async Task InteractionPerformanceTestAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var results = new List<object>();
        var process = Process.GetCurrentProcess();
        var originalPosition = AppWindow.Position;
        var originalSize = AppWindow.Size;
        try
        {
            Activate(); await Task.Delay(250);
            await Sample("home-idle", () => Task.Delay(250));
            await Sample("home-window-move", async () =>
            {
                for (var i = 0; i < 24; i++)
                {
                    AppWindow.Move(new(originalPosition.X + (i % 8) * 3, originalPosition.Y));
                    await Task.Delay(16);
                }
                AppWindow.Move(originalPosition);
            });
            foreach (var count in new[] { 20, 1000, 5000 })
            {
                var document = new MapDocument();
                Topic? branch = null;
                for (var i = 1; i < count; i++)
                {
                    var topic = new Topic { Text = "中文主题 " + i };
                    if (i % 10 == 1) { branch = topic; document.Sheets[0].Root.Children.Add(topic); }
                    else branch!.Children.Add(topic);
                }
                AddSession(new(document)); await MapView.Ready;
                MapView.ChangeZoom(1 / MapView.Zoom); MapView.CenterOn(active.Sheet.Root.Id);
                await Task.Delay(150);
                await Sample($"{count}-pan-zoom", async () =>
                {
                    for (var i = 0; i < 24; i++)
                    {
                        MapView.PanForValidation(4, 1);
                        MapView.ChangeZoom(i % 2 == 0 ? 1.025 : 1 / 1.025);
                        await Task.Delay(16);
                    }
                });
                active.Select(active.Sheet.Root.Id); MapView.BeginEdit(); await Task.Delay(100);
                var beforeDraft = ModelJson.Serialize(active.Document);
                await Sample($"{count}-typing", async () =>
                {
                    for (var i = 0; i < 20; i++)
                        await MapView.SetEditorTextForValidationAsync("连续中文输入测试 " + new string('字', i));
                });
                if (beforeDraft != ModelJson.Serialize(active.Document)) throw new InvalidOperationException("输入预览提前修改了文档。");
                if (string.Concat(MapView.Scene.Nodes.First(n => n.Topic.Id == active.Sheet.Root.Id).Lines) != "连续中文输入测试 " + new string('字', 19))
                    throw new InvalidOperationException("绘制前没有使用最新的输入预览。");
                MapView.CommitEdit();
                if (!active.Sheet.Root.Text.EndsWith(new string('字', 19))) throw new InvalidOperationException("输入提交遗漏了最新文字。");
                active.Undo();
                if (ModelJson.Serialize(active.Document) != beforeDraft) throw new InvalidOperationException("文字输入不能一次撤销。");
                var beforeBurst = ModelJson.Serialize(active.Document);
                await Sample($"{count}-command-burst", () =>
                {
                    for (var i = 0; i < 12; i++) active.Execute("连续编辑", _ => active.Sheet.Root.Notes = "备注 " + i);
                    return Task.CompletedTask;
                });
                if (!MapView.Scene.Nodes.First(n => n.Topic.Id == active.Sheet.Root.Id).Badges.Contains('≡'))
                    throw new InvalidOperationException("合并刷新后场景仍包含旧内容。");
                for (var i = 0; i < 12; i++) active.Undo();
                if (ModelJson.Serialize(active.Document) != beforeBurst) throw new InvalidOperationException("合并刷新改变了独立命令的撤销顺序。");
                await Sample($"{count}-resize", async () =>
                {
                    for (var i = 0; i < 12; i++)
                    {
                        AppWindow.Resize(new(originalSize.Width + i % 2 * 12, originalSize.Height));
                        await Task.Delay(16);
                    }
                    AppWindow.Resize(originalSize);
                });
                recoveryTimer.Stop();
            }
            AtomicFile.WriteText(Path.Combine(directory, "result.json"), ModelJson.Serialize(new { success = true,
                runtime = Environment.Version.ToString(), forceSoftware = MapView.Device.ForceSoftwareRenderer, results,
                note = "原生窗口内的脚本驱动操作；dispatcher 为后台探针到 UI 队列的等待时间，canvas 为布局/绘制回调的 CPU 耗时。窗口移动是程序调用，均不代表鼠标到显示器延迟或实际显示帧率。" }));
        }
        catch (Exception ex)
        {
            AtomicFile.WriteText(Path.Combine(directory, "result.json"), ModelJson.Serialize(new { success = false, results, error = ex.ToString() }));
        }
        finally { recoveryTimer.Stop(); AppWindow.Move(originalPosition); AppWindow.Resize(originalSize); }

        async Task Sample(string name, Func<Task> action)
        {
            await using var probe = new DispatcherProbe(DispatcherQueue);
            probe.Start(); await Task.Delay(40);
            var before = MapView.WorkSnapshot; var refreshBefore = refreshCount;
            var cpuBefore = process.TotalProcessorTime; var watch = Stopwatch.StartNew();
            await action(); var actionMs = watch.Elapsed.TotalMilliseconds;
            await Task.Delay(100);
            results.Add(new { name, actionMs, elapsedMs = watch.Elapsed.TotalMilliseconds,
                cpuMs = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds, dispatcher = probe.Result(),
                canvas = MapView.WorkSnapshot.Since(before), uiRefreshes = refreshCount - refreshBefore });
        }
    }
}
