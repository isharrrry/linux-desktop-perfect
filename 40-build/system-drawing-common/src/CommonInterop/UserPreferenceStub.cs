// 【ACC-028】`Microsoft.Win32.UserPreferenceChangedEventArgs` 的最小桩。
//   `System.Drawing.Internal.SystemColorTracker` 订阅 Windows 的"用户偏好变更"事件来刷新系统色。
//   Linux 上没有这个机制；桩只让签名编译通过，事件永不触发（系统色用静态初值即可）。
namespace Microsoft.Win32
{
    public enum UserPreferenceCategory { Accessibility = 1, Color = 2, Desktop = 3, General = 4, Icon = 5, Keyboard = 6, Menu = 7, Mouse = 8, Policy = 9, Power = 10, Screensaver = 11, Window = 12, Locale = 13, VisualStyle = 14 }

    public class UserPreferenceChangedEventArgs : System.EventArgs
    {
        public UserPreferenceChangedEventArgs(UserPreferenceCategory category) { Category = category; }
        public UserPreferenceCategory Category { get; }
    }

    public delegate void UserPreferenceChangedEventHandler(object sender, UserPreferenceChangedEventArgs e);

    public sealed class SystemEvents
    {
        public static event UserPreferenceChangedEventHandler UserPreferenceChanged
        {
            add { /* Linux 无此事件源 */ }
            remove { }
        }
    }
}
