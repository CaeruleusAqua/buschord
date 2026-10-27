using System.IO.Compression;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Interactivity;
using Knx.Catalog;
using Knx;
using Knx.App.Services;
using Knx.App.Views;

internal static class NativeDraftChecks
{
    private static bool _shots;

    public static int Run(bool shots = false)
    {
        _shots = shots;
        AppBuilder.Configure<Application>().UsePlatformDetect()
            .AfterSetup(_ => DispatcherTimer.RunOnce(Check, TimeSpan.FromMilliseconds(100)))
            .StartWithClassicDesktopLifetime([], ShutdownMode.OnExplicitShutdown);
        return Environment.ExitCode;
    }

    private static async void Check()
    {
        var lifetime = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        var root = Path.Combine(Path.GetTempPath(), "buschord-native-draft-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var failures = 0;
        void Assert(string name, bool ok) { Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}"); if (!ok) failures++; }
        try
        {
            var settings = Path.Combine(root, "settings.json");
            File.WriteAllText(settings, "{}");
            Environment.SetEnvironmentVariable("BUSCHORD_SETTINGS", settings);
            typeof(CatalogLibrary).GetField("_store", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(CatalogLibrary.Shared, new CatalogStore(Path.Combine(root, "catalog")));
            Application.Current.Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
            Application.Current.Styles.Add(new StyleInclude(new Uri("avares://BusChord/"))
            {
                Source = new Uri("avares://BusChord/Workbench/WorkbenchTheme.axaml"),
            });
            failures += await TimeParameterChecks.RunNative(root);
            failures += await StorageDiagnosticsChecks.RunNative(root);
            var project = new Project { Id = "P", Name = "Native draft regression" };
            var installation = new Installation { Id = "I" };
            var area = new Area { Id = "A", Address = 1 };
            var line = new Line { Id = "L", Address = 1 };
            foreach (var name in new[] { "A", "B" })
                line.Devices.Add(new DeviceInstance { Id = name, Name = name, Address = new(1, 1, (byte)(line.Devices.Count + 1)), ProductRefId = "M-000F_A-0001-01-0000" });
            area.Lines.Add(line); installation.Areas.Add(area); project.Installations.Add(installation);
            var path = Path.Combine(root, "fixture.knxproj");
            Knxproj.Save(project, path);
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
            using (var stream = zip.CreateEntry("M-000F/M-000F_A-0001-01-0000.xml").Open())
                XDocument.Parse("<KNX><ApplicationProgram><Static><ParameterTypes><ParameterType Id='N' Name='Number'><TypeNumber Type='unsignedInt' SizeInBit='8' minInclusive='0' maxInclusive='100'/></ParameterType></ParameterTypes><Parameters><Parameter Id='P-N' Name='Brightness' ParameterType='N' Value='25'/></Parameters><ParameterRefs><ParameterRef Id='R-N' RefId='P-N'/></ParameterRefs></Static></ApplicationProgram></KNX>").Save(stream);
            // This identity-isolation fixture deliberately has duplicate device IDs.
            // Keep its synthetic in-memory tree separate from XML merge/roundtrip tests,
            // but supply the immutable completed archive used for parameter declarations.
            var importedFixture = Knxproj.Load(path);
            var archiveState = typeof(Project).GetProperty("ArchiveState", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            archiveState.SetValue(project, archiveState.GetValue(importedFixture));
            line.Devices[1] = new DeviceInstance { Id = "A", Name = "B", Address = new(1, 1, 2), ProductRefId = "M-000F_A-0001-01-0000" };
            var vm = new MainWindowViewModel(); vm.CreateProject(project, null); vm.ProjectPath = path;
            var a = vm.Devices[0]; var b = vm.Devices[1];
            var pane = new ParameterPane(vm);
            var window = new Window { Content = pane, Width = 700, Height = 500 };
            window.Show();
            ParameterItemViewModel Row() => ((IEnumerable<ParameterItemViewModel>)pane.FindControl<ItemsControl>("ParameterList")!.ItemsSource!).Single();
            pane.Load(a);
            await Task.Delay(300);
            var editor = pane.GetVisualDescendants().OfType<TextBox>().First();
            editor.Focus(); editor.SelectAll();
            editor.SelectedText = "44";
            await Task.Delay(100);
            Assert("Native text edit is dirty", pane.HasUnappliedChanges && Row().Value == "44");
            editor.Undo();
            await Task.Delay(100);
            Assert("Local text undo to baseline is clean", Row().Value == "25" && !pane.HasUnappliedChanges);
            pane.DiscardDrafts(); pane.Load(b); Row().Value = "46"; pane.Load(a); Row().Value = "47";
            System.ComponentModel.PropertyChangedEventHandler reenter = (_, e) => { if (e.PropertyName == nameof(MainWindowViewModel.CanUndo)) pane.Load(b); };
            vm.PropertyChanged += reenter;
            var applied = pane.Apply(); vm.PropertyChanged -= reenter;
            Assert("Native Apply callback selects B and preserves dirty ownership", applied && pane.Device == b.Device && Row().Value == "46" && pane.HasUnappliedChanges);
            Assert("Only A was applied", a.Device.ParameterValues.GetValueOrDefault("R-N") == "47" && b.Device.ParameterValues.Count == 0);
            pane.Load(a);
            Assert("Applied A reloads from the model", Row().Value == "47");
            pane.Load(b);
            Assert("Native reentrant Apply preserves B across subsequent switches", Row().Value == "46" && pane.HasUnappliedChanges);
            Row().Value = "25";
            Assert("Only B was dirty after Apply; reverting B leaves no hidden A draft", !pane.HasUnappliedChanges);
            pane.DiscardDrafts();
            Assert("Discarding B leaves applied A clean", !pane.HasUnappliedChanges);
            pane.Load(a); Row().Value = "55";
            pane.Load(b); Row().Value = "56";
            pane.FindControl<Button>("DiscardButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert("C6 local native Discard restores only B", Row().Value == "25" && pane.HasUnappliedChanges);
            pane.Load(a);
            Assert("C6 local native Discard preserves hidden A draft", Row().Value == "55");
            pane.Load(b); Row().Value = "57"; pane.Load(a);
            pane.DiscardDrafts();
            pane.Load(b);
            Assert("C6 explicit project-wide discard also clears hidden drafts", Row().Value == "25" && !pane.HasUnappliedChanges);
            pane.Load(a);
            Assert("C6 project-wide discard also restores the current device", Row().Value == "47" && !pane.HasUnappliedChanges);
            pane.Load(a); Row().Value = "70";
            var reentered = false;
            System.ComponentModel.PropertyChangedEventHandler replaceDraft = (_, e) =>
            {
                if (reentered || e.PropertyName != nameof(MainWindowViewModel.CanUndo)) return;
                reentered = true;
                pane.Load(b); pane.Load(a); Row().Value = "71"; pane.Load(b);
            };
            vm.PropertyChanged += replaceDraft;
            applied = pane.Apply();
            vm.PropertyChanged -= replaceDraft;
            Assert("Apply retains a newer same-device draft while hidden", applied && reentered && pane.HasUnappliedChanges);
            pane.Load(a);
            Assert("Newer same-device draft survives Apply cleanup", Row().Value == "71"
                && a.Device.ParameterValues.GetValueOrDefault("R-N") == "70" && pane.HasUnappliedChanges);
            void Click(string name) => pane.FindControl<Button>(name)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Click("ReviewButton");
            Assert("Native review panel shows proposed parameter diff without applying", pane.FindControl<StackPanel>("PreviewPanel")!.IsVisible
                && pane.Session.Preview!.Changes.Single() is { Before: "70", After: "71", Error: null }
                && a.Device.ParameterValues["R-N"] == "70");
            Click("CancelReviewButton");
            Assert("Native cancel review preserves draft and model", !pane.FindControl<StackPanel>("PreviewPanel")!.IsVisible
                && pane.HasUnappliedChanges && Row().Value == "71" && a.Device.ParameterValues["R-N"] == "70");
            Row().Value = "101"; Click("ReviewButton");
            Assert("Native invalid preview blocks Apply", !pane.FindControl<Button>("ApplyButton")!.IsEnabled
                && pane.Session.Preview?.IsValid == false && a.Device.ParameterValues["R-N"] == "70");
            await Task.Delay(100);
            var expectedError = pane.Session.ValidationErrors.Single().Error;
            var fieldError = pane.GetVisualDescendants().OfType<TextBlock>()
                .FirstOrDefault(t => t.Classes.Contains("wb-error") && t.Text == expectedError);
            Assert("Native invalid field renders its error and the footer summary",
                fieldError is not null
                && pane.FindControl<TextBlock>("ValidationSummary")!.IsVisible
                && pane.FindControl<Border>("DraftPill")!.IsVisible);
            var editing = pane.Session;
            window.Content = null; pane.Dispose();
            var detachedMessage = pane.FindControl<TextBlock>("MessageText")!.Text;
            pane = new ParameterPane(editing); window.Content = pane;
            await Task.Delay(150);
            Assert("Native fresh view retains invalid draft and validation", Row().Value == "101"
                && pane.FindControl<TextBlock>("MessageText")!.Text == detachedMessage && pane.HasUnappliedChanges);
            using var peer = new ParameterPane(editing);
            var peerWindow = new Window { Content = peer, Width = 700, Height = 500 };
            peerWindow.Show();
            await Task.Delay(150);
            Row().Value = "72";
            await Task.Delay(100); // Validation can change the virtualized viewport before the next layout pass.
            Assert("Two native views update the same draft without recreating editors", peer.GetVisualDescendants()
                .OfType<NumericUpDown>().Single().Value == 72 && Row().Value == "72");
            Click("ReviewButton"); Click("ApplyButton");
            Assert("Native reviewed Apply commits once and clears draft", a.Device.ParameterValues["R-N"] == "72"
                && !editing.HasUnappliedChanges && !pane.FindControl<StackPanel>("PreviewPanel")!.IsVisible);
            vm.UndoCommand.Execute(null);
            Assert("One undo restores reviewed Apply", vm.Devices[0].Device.ParameterValues["R-N"] == "70");

            // Long programs: the list virtualizes its rows, so only the visible editors exist, and
            // a section jump still scrolls to a row without a realized container.
            var longPath = Path.Combine(root, "long.knxproj");
            var longProject = new Project { Id = "P-LONG", Name = "Long" };
            var longInstallation = new Installation { Id = "I-L" };
            var longArea = new Area { Id = "A-L", Address = 1 };
            var longLine = new Line { Id = "L-L", Address = 1 };
            longLine.Devices.Add(new DeviceInstance { Id = "LONG", Name = "Long", Address = new(1, 1, 1), ProductRefId = "M-0LNG_A-1" });
            longArea.Lines.Add(longLine); longInstallation.Areas.Add(longArea); longProject.Installations.Add(longInstallation);
            Knxproj.Save(longProject, longPath);
            var longXml = new System.Text.StringBuilder("<KNX><ApplicationProgram Id=\"M-0LNG_A-1\"><Static><ParameterTypes>");
            longXml.Append("<ParameterType Id=\"N\" Name=\"Number\"><TypeNumber Type=\"unsignedInt\" SizeInBit=\"8\" minInclusive=\"0\" maxInclusive=\"255\"/></ParameterType></ParameterTypes><Parameters>");
            for (var i = 0; i < 60; i++) longXml.Append($"<Parameter Id=\"P{i}\" Name=\"Kanal {(char)('A' + i % 26)}: Parameter {i}\" ParameterType=\"N\" Value=\"1\"/>");
            longXml.Append("</Parameters><ParameterRefs>");
            for (var i = 0; i < 60; i++) longXml.Append($"<ParameterRef Id=\"R{i}\" RefId=\"P{i}\"/>");
            longXml.Append("</ParameterRefs></Static><Dynamic><choose/></Dynamic></ApplicationProgram></KNX>");
            using (var zip = ZipFile.Open(longPath, ZipArchiveMode.Update))
            using (var stream = zip.CreateEntry("M-0LNG/M-0LNG_A-1.xml").Open())
            using (var writer = new StreamWriter(stream))
                writer.Write(longXml.ToString());
            var longVm = new MainWindowViewModel();
            longVm.CreateProject(Knxproj.Load(longPath), null);
            longVm.ProjectPath = longPath;
            using var longPane = new ParameterPane(longVm);
            var longWindow = new Window { Content = longPane, Width = 700, Height = 500 };
            longWindow.Show();
            longPane.Load(longVm.Devices[0]);
            await Task.Delay(400);
            var longRows = longPane.Session.Rows.Length;
            var longEditors = longPane.GetVisualDescendants().OfType<NumericUpDown>().Count();
            Assert("Virtualized parameter list realizes only the visible editors",
                longRows == 60 && longEditors > 0 && longEditors < longRows / 2);
            var sectionButtons = longPane.FindControl<ItemsControl>("SectionList")!
                .GetVisualDescendants().OfType<Button>().Where(b => b.Tag is string).ToArray();
            var parameterScroll = longPane.FindControl<ScrollViewer>("ParameterScroll")!;
            sectionButtons[2].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(300);
            Assert("Section jump scrolls the virtualized list",
                sectionButtons.Length > 3 && parameterScroll.Offset.Y > 0
                && longPane.Session.SelectedSection == (string)sectionButtons[2].Tag!);
            longWindow.Close();

            var structurePath = Path.Combine(root, "structure.knxproj");
            Knxproj.Save(Knxproj.Load(path), structurePath);
            using (var zip = ZipFile.Open(structurePath, ZipArchiveMode.Update))
            {
                zip.GetEntry("M-000F/M-000F_A-0001-01-0000.xml")?.Delete();
                using var stream = zip.CreateEntry("M-000F/M-000F_A-0001-01-0000.xml").Open();
                XDocument.Parse("""
                    <KNX><ApplicationProgram Id="APP"><Static><ParameterTypes><ParameterType Id="N"><TypeNumber Type="unsignedInt" SizeInBit="8" /></ParameterType></ParameterTypes>
                    <Parameters><Parameter Id="P-count" Text="Count" ParameterType="N" Value="20" /></Parameters><ParameterRefs><ParameterRef Id="R-count" RefId="P-count" /></ParameterRefs>
                    <ModuleDefs><ModuleDef Id="APP_MD-1"><Static><Parameters><Parameter Id="APP_MD-1_P-1" Text="Value" ParameterType="N" Value="1" /></Parameters>
                    <ParameterRefs><ParameterRef Id="APP_MD-1_R-1" RefId="APP_MD-1_P-1" /></ParameterRefs></Static><Dynamic><ParameterRefRef RefId="APP_MD-1_R-1" /></Dynamic></ModuleDef></ModuleDefs></Static>
                    <Dynamic><ParameterRefRef RefId="R-count" /><Repeat Id="Repeat" Count="0" ParameterRefId="R-count"><Module Id="APP_MD-1_M-1" RefId="APP_MD-1" /></Repeat></Dynamic></ApplicationProgram></KNX>
                    """).Save(stream);
            }
            var structureVm = new MainWindowViewModel(); structureVm.CreateProject(Knxproj.Load(structurePath), null); structureVm.ProjectPath = structurePath;
            using var structurePane = new ParameterPane(structureVm);
            var structureWindow = new Window { Content = structurePane, Width = 700, Height = 700 };
            structureWindow.Show(); structurePane.Load(structureVm.Devices[0]);
            await Task.Delay(300);
            var nativeCount = structurePane.GetVisualDescendants().OfType<NumericUpDown>().First();
            var nativeCountText = nativeCount.GetVisualDescendants().OfType<TextBox>().First();
            nativeCountText.Focus(); nativeCount.Value = 21;
            await Task.Delay(200);
            Assert("Native count growth retains the focused editor and publishes all rows", structurePane.Session.Rows.Length == 22
                && ReferenceEquals(nativeCount, structurePane.GetVisualDescendants().OfType<NumericUpDown>().First()) && nativeCount.IsKeyboardFocusWithin);
            var structureScroll = structurePane.FindControl<ScrollViewer>("ParameterScroll")!;
            structureScroll.Offset = new Vector(0, 250);
            await Task.Delay(100);
            var scrollBefore = structureScroll.Offset.Y;
            structurePane.Session.Rows.Single(r => r.Instance.Id == "R-count").Value = "22";
            await Task.Delay(200);
            Assert("Native structural growth preserves the scroll position", structurePane.Session.Rows.Length == 23
                && scrollBefore > 0 && Math.Abs(structureScroll.Offset.Y - scrollBefore) < 1);
            structurePane.Session.Rows.Single(r => r.Instance.Id == "R-count").Value = "257";
            await Task.Delay(100);
            Assert("Native structure errors keep Discard visible and Apply disabled", structurePane.FindControl<Button>("DiscardButton")!.IsVisible
                && structurePane.FindControl<Button>("DiscardButton")!.IsEnabled && !structurePane.FindControl<Button>("ApplyButton")!.IsEnabled);
            structurePane.FindControl<Button>("DiscardButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert("Native Discard recovers from a failed count", structurePane.Session.Rows.Single(r => r.Instance.Id == "R-count").Value == "20"
                && structurePane.Session.Rows.Length == 21 && structurePane.Session.CanApply);
            structureWindow.Close();

            if (_shots)
            {
                peerWindow.Hide();
                pane.Load(vm.Devices[0]);
                await Task.Delay(100);
                Row().Value = "88";
                await Task.Delay(100);
                var shots = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Temp", "opencode", "param-shots");
                Directory.CreateDirectory(shots);
                await Capture(window, 1440, 900, ThemeVariant.Light, Path.Combine(shots, "pane-light-1440.png"));
                await Capture(window, 1024, 700, ThemeVariant.Light, Path.Combine(shots, "pane-light-1024.png"));
                await Capture(window, 1440, 900, ThemeVariant.Dark, Path.Combine(shots, "pane-dark-1440.png"));
                await Capture(window, 1024, 700, ThemeVariant.Dark, Path.Combine(shots, "pane-dark-1024.png"));
            }
            peerWindow.Close();
            pane.Dispose();
            window.Close();
        }
        catch (Exception ex) { Console.WriteLine(ex); failures++; }
        finally { Directory.Delete(root, true); }
        Console.WriteLine(failures == 0 ? "NATIVE DRAFT CHECKS OK" : $"{failures} native failures");
        lifetime.Shutdown(failures == 0 ? 0 : 1);
    }

    /// <summary>Optional --shots: renders the real pane at the review sizes for light and dark.</summary>
    private static async Task Capture(Window window, int width, int height, ThemeVariant theme, string path)
    {
        window.RequestedThemeVariant = theme;
        window.Width = width;
        window.Height = height;
        await Task.Delay(250);
        using var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        bitmap.Render(window);
        bitmap.Save(path);
        Console.WriteLine($"Screenshot: {path} ({width}x{height})");
    }
}
