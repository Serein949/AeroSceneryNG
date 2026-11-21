using System;
using System.Windows;
using System.Windows.Threading;
using AeroSceneryNG.Core;

namespace AeroSceneryNG
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            try { Loc.Language = AppSettings.Load().Language; } catch { }
            DispatcherUnhandledException += OnUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += (s, args) => { };
        }

        /// <summary>Swaps the palette dictionary while keeping the control styles in place.</summary>
        public static void ApplyTheme(bool dark)
        {
            try
            {
                var dictionaries = Current.Resources.MergedDictionaries;
                var palette = new ResourceDictionary
                {
                    Source = new Uri(dark ? "Theme/Dark.xaml" : "Theme/Light.xaml", UriKind.Relative)
                };

                for (int i = 0; i < dictionaries.Count; i++)
                {
                    string source = dictionaries[i].Source != null ? dictionaries[i].Source.OriginalString : "";
                    if (source.IndexOf("Theme/Dark.xaml", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        source.IndexOf("Theme/Light.xaml", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        dictionaries[i] = palette;
                        return;
                    }
                }

                dictionaries.Add(palette);
            }
            catch { }
        }

        private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            try
            {
                try
                {
                    System.IO.File.WriteAllText(
                        System.IO.Path.Combine(AppContext.BaseDirectory, "crash.log"),
                        e.Exception.ToString());
                }
                catch { }

                MessageBox.Show(
                    Loc.F("发生了一个未处理的错误：\n\n{0}\n\n{1}", e.Exception.Message, e.Exception.StackTrace),
                    "AeroScenery NG", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch { }
            e.Handled = true;
        }
    }
}
