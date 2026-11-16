using System.ComponentModel;
using System.Diagnostics;

namespace NetPilot.Tests;

internal static class ApplicationReconnectLauncherTests
{
    internal static async Task RunAsync(Action<string, bool, string> report)
    {
        async Task Check(string name, Func<Task> action)
        { try { await action(); report(name, true, ""); } catch (Exception ex) { report(name, false, ex.Message); } }
        await Check("manual reconnect launches only the fixed elevated helper with a caller identity lease", async () =>
        {
            int calls = 0;
            var launcher = new ApplicationReconnectLauncher(info =>
            {
                calls++;
                Require(info.UseShellExecute && info.Verb == "runas" && info.WindowStyle == ProcessWindowStyle.Hidden,
                    "The reconnect helper lost its explicit UAC launch.");
                Require(info.FileName == Path.Combine(AppContext.BaseDirectory, "RtlineDnsPilot.exe") && info.Arguments.Length == 0,
                    "The helper executable or structured arguments changed.");
                var request = ApplicationReconnectLauncher.Parse(info.ArgumentList.ToArray());
                using var current = Process.GetCurrentProcess();
                Require(request.Targets == (ApplicationReconnectTargets.Chrome | ApplicationReconnectTargets.Codex)
                    && request.CallerId == current.Id && request.CallerStartUtcTicks == current.StartTime.ToUniversalTime().Ticks,
                    "The original caller identity or selected applications were lost.");
                return Task.FromResult(0);
            });
            Require(await launcher.RunAsync(ApplicationReconnectTargets.Chrome | ApplicationReconnectTargets.Codex) == ApplicationReconnectOutcome.Completed
                && calls == 1, "The manual helper was not invoked exactly once.");
        });
        await Check("reconnect rejects empty selection and malformed or extra CLI arguments before launch", async () =>
        {
            int calls = 0;
            var launcher = new ApplicationReconnectLauncher(_ => { calls++; return Task.FromResult(0); });
            foreach (var target in new[] { ApplicationReconnectTargets.None, (ApplicationReconnectTargets)4, (ApplicationReconnectTargets)(-1) })
            {
                try { await launcher.RunAsync(target); throw new Exception("Invalid selection reached launch."); }
                catch (ArgumentException) { }
            }
            Require(calls == 0, "Invalid input invoked an elevated process.");
            foreach (var args in new[] {
                new[] { ApplicationReconnectLauncher.Command, "--targets", "3", "--caller-pid", "12", "--caller-start", "1", "--service" },
                new[] { ApplicationReconnectLauncher.Command, "--targets", "4", "--caller-pid", "12", "--caller-start", "1" },
                new[] { ApplicationReconnectLauncher.Command, "--targets", "1", "--caller-pid", "0", "--caller-start", "1" },
                new[] { ApplicationReconnectLauncher.Command, "--targets", "1", "--caller-pid", "12", "--caller-start", "-1" },
                new[] { ApplicationReconnectLauncher.Command, "--targets", "1", "--caller-pid", "12", "--caller-start", "9223372036854775807" } })
            {
                try { ApplicationReconnectLauncher.Parse(args); throw new Exception("Malformed request was accepted."); }
                catch (ArgumentException) { }
            }
        });
        await Check("cancelled UAC does not replay the reconnect action", async () =>
        {
            int calls = 0;
            var launcher = new ApplicationReconnectLauncher(_ => { calls++; throw new Win32Exception(1223); });
            Require(await launcher.RunAsync(ApplicationReconnectTargets.Codex) == ApplicationReconnectOutcome.Cancelled && calls == 1,
                "UAC cancellation became success or replayed the action.");
        });
        await Check("helper partial, empty and IPv6-limited outcomes remain distinct", async () =>
        {
            foreach (var (code, expected) in new[] { (10, ApplicationReconnectOutcome.NoConnections),
                (11, ApplicationReconnectOutcome.Partial), (12, ApplicationReconnectOutcome.Ipv6Remaining) })
                Require(await new ApplicationReconnectLauncher(_ => Task.FromResult(code)).RunAsync(ApplicationReconnectTargets.Chrome) == expected,
                    "A partial result became a complete reconnect.");
            try
            {
                await new ApplicationReconnectLauncher(_ => Task.FromResult(1)).RunAsync(ApplicationReconnectTargets.Codex);
                throw new Exception("A failed helper became success.");
            }
            catch (IOException) { }
        });
        await Check("reconnect result does not claim completion when IPv6 or write errors remain", () =>
        {
            Require(ApplicationReconnectLauncher.ResultCode(new(2, 3, 1, 0, 0)) == 11
                && ApplicationReconnectLauncher.ResultCode(new(2, 3, 0, 0, 4)) == 12
                && ApplicationReconnectLauncher.ResultCode(new(0, 0, 0, 0, 0)) == 10,
                "An incomplete native result was marked complete.");
            var text = ApplicationReconnectLauncher.ResultMessage(new(2, 3, 1, 2, 4));
            Require(text.Contains("Ошибок: 1") && text.Contains("IPv6") && text.Contains("QUIC") && text.Contains("внутренний DNS-кэш"),
                "The native result hides its limitations.");
            return Task.CompletedTask;
        });
        await Check("reconnect dialog requires an application selection and shows its connection limits", CheckDialogAsync);
    }

    private static Task CheckDialogAsync()
    {
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var dialog = new ApplicationReconnectDialog();
                var controls = Children(dialog).ToArray();
                var choices = controls.OfType<CheckBox>().ToArray();
                var action = controls.OfType<Button>().Single(button => button.DialogResult == DialogResult.OK);
                Require(choices.Length == 2 && dialog.Targets == (ApplicationReconnectTargets.Chrome | ApplicationReconnectTargets.Codex),
                    "The application choices changed.");
                foreach (var choice in choices) choice.Checked = false;
                Require(dialog.Targets == ApplicationReconnectTargets.None && !action.Enabled, "Empty reconnect selection remains enabled.");
                choices[1].Checked = true;
                Require(action.Enabled && dialog.Targets == ApplicationReconnectTargets.Codex, "The selected app was not retained.");
                var text = string.Join(" ", controls.OfType<Label>().Select(label => label.Text));
                Require(text.Contains("других вкладок") && text.Contains("IPv6") && text.Contains("QUIC") && text.Contains("администратора"),
                    "The dialog hides the impact or limitations.");
                dialog.Show();
                Application.DoEvents();
                using var bitmap = new Bitmap(dialog.Width, dialog.Height);
                dialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, dialog.Size));
                var image = Path.Combine(Path.GetTempPath(), "Rtline-reconnect-dialog-" + Guid.NewGuid().ToString("N") + ".png");
                bitmap.Save(image, System.Drawing.Imaging.ImageFormat.Png);
                Console.WriteLine("INFO reconnect dialog preview: " + image);
                dialog.Close();
                complete.SetResult();
            }
            catch (Exception ex) { complete.SetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return complete.Task;
    }

    private static IEnumerable<Control> Children(Control parent)
    { foreach (Control child in parent.Controls) { yield return child; foreach (var nested in Children(child)) yield return nested; } }
    private static void Require(bool condition, string reason) { if (!condition) throw new Exception(reason); }
}
