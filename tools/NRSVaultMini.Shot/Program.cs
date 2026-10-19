// Renders the NRSVault Mini main window headlessly to PNG files (developer visual check).
//   NRSVaultMini.Shot <nes20db.xml> <collection root> <out folder>
using Avalonia;
using Avalonia.Headless;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NRSVaultMini;

AppBuilder.Configure<App>().UseSkia().WithInterFont()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .SetupWithoutStarting();

var win = new MainWindow { Width = 1600, Height = 950 };
var vm = (MainViewModel)win.DataContext!;
vm.XmlPath = args[0];
vm.RootFolder = args[1];
win.Show();

void Pump(Task t)
{
    while (!t.IsCompleted) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(20); }
    for (int i = 0; i < 30; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
}
void Shot(string name)
{
    Pump(Task.Delay(400));
    win.CaptureRenderedFrame()?.Save(Path.Combine(args[2], name));
    Console.WriteLine("saved " + name);
}

void Idle() { Pump(Task.Delay(200)); while (vm.IsBusy) Pump(Task.Delay(50)); }
Idle();
Pump(vm.LoadDbAsync());
Idle();
Pump(vm.ScanAsync());
Idle();
var top = vm.Visible.FirstOrDefault(n => n.Name == "Licensed Japan");
if (top != null) vm.Toggle(top);
Shot("main.png");

vm.Filter = vm.FilterOptions.First(f => f.Name == "Needs attention (not OK)");
vm.Selected = vm.Visible.FirstOrDefault(n => !n.IsFolder);
Shot("filtered.png");

vm.Filter = vm.FilterOptions[0];
vm.SortBy("size"); vm.SortBy("size"); // descending
Shot("sorted.png");

if (args.Length > 3)
{
    vm.SortBy("name");
    vm.AddSourceFolder(args[3]);
    Pump(vm.ScanSourcesAsync());
    Idle();
    vm.SourcesOnlyNeeded = false;
    win.GetVisualDescendants().OfType<TabControl>().First().SelectedIndex = 1;
    Shot("sources.png");
}
