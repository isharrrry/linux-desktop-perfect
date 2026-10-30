using System;
using System.Windows;

namespace LinuxDesktopPerfect.Samples.WpfFirstWindow
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            string theme = Arg("--theme");
            if (theme == null) return;
            Uri uri = ThemeUri(theme);
            if (uri == null) return;
            Resources.MergedDictionaries.Clear();
            Resources.MergedDictionaries.Add(new ResourceDictionary { Source = uri });
        }

        private static Uri ThemeUri(string theme)
        {
            switch (theme)
            {
                case "Classic":   return new Uri("pack://application:,,,/PresentationFramework.Classic;component/themes/Classic.xaml");
                case "Aero":      return new Uri("pack://application:,,,/PresentationFramework.Aero;component/themes/Aero.NormalColor.xaml");
                case "Aero2":     return new Uri("pack://application:,,,/PresentationFramework.Aero2;component/Themes/Aero2.NormalColor.xaml");
                case "AeroLite":  return new Uri("pack://application:,,,/PresentationFramework.AeroLite;component/Themes/AeroLite.NormalColor.xaml");
                case "Luna":      return new Uri("pack://application:,,,/PresentationFramework.Luna;component/Themes/Luna.NormalColor.xaml");
                case "Royale":    return new Uri("pack://application:,,,/PresentationFramework.Royale;component/Themes/Royale.NormalColor.xaml");
                default:          return null;
            }
        }

        private static string Arg(string name)
        {
            string[] a = Environment.GetCommandLineArgs();
            for (int i = 0; i < a.Length - 1; i++) if (a[i] == name) return a[i + 1];
            return null;
        }
    }
}
