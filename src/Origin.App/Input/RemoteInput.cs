using System.Windows.Input;

namespace Origin.Input
{
    /// <summary>What the user asked for, whatever the device: keyboard, Media Center remote or media keys.</summary>
    public enum RemoteCommand
    {
        None,
        Up,
        Down,
        Left,
        Right,
        Ok,
        Back,
        Home,
        Guide,
        LiveTv,
        Info,
        ChannelUp,
        ChannelDown,
        PlayPause,
        Play,
        Pause,
        Stop,
        FullScreen,
        Digit0,
        Digit1,
        Digit2,
        Digit3,
        Digit4,
        Digit5,
        Digit6,
        Digit7,
        Digit8,
        Digit9,
    }

    /// <summary>
    /// Translates keys and WM_APPCOMMAND messages into <see cref="RemoteCommand"/>s, following
    /// the Windows Media Center keyboard shortcuts that eHome/MCE remotes emit.
    /// </summary>
    public static class RemoteInput
    {
        public const int WM_APPCOMMAND = 0x0319;

        public static bool IsDigit(RemoteCommand command) => command >= RemoteCommand.Digit0 && command <= RemoteCommand.Digit9;

        public static int ToDigit(RemoteCommand command) => command - RemoteCommand.Digit0;

        /// <param name="textEntry">True while a text box has focus, so letters, digits and editing keys stay with it.</param>
        public static RemoteCommand FromKey(KeyEventArgs e, bool textEntry)
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            var modifiers = Keyboard.Modifiers;
            var ctrl = (modifiers & ModifierKeys.Control) != 0;
            var shift = (modifiers & ModifierKeys.Shift) != 0;
            var alt = (modifiers & ModifierKeys.Alt) != 0;

            if (alt && key == Key.Enter)
            {
                return RemoteCommand.FullScreen;
            }

            switch (key)
            {
                case Key.F11:
                    return RemoteCommand.FullScreen;
                case Key.Escape:
                case Key.BrowserBack:
                    return RemoteCommand.Back;
                case Key.BrowserHome:
                    return RemoteCommand.Home;
                case Key.MediaPlayPause:
                    return RemoteCommand.PlayPause;
                case Key.MediaStop:
                    return RemoteCommand.Stop;
                case Key.PageUp:
                    return RemoteCommand.ChannelUp;
                case Key.PageDown:
                    return RemoteCommand.ChannelDown;
                case Key.Up:
                    return RemoteCommand.Up;
                case Key.Down:
                    return RemoteCommand.Down;
            }

            if (ctrl)
            {
                switch (key)
                {
                    case Key.G:
                        return RemoteCommand.Guide;
                    case Key.T:
                        return RemoteCommand.LiveTv;
                    case Key.D:
                        return RemoteCommand.Info;
                    case Key.P:
                        return shift ? RemoteCommand.Play : RemoteCommand.Pause;
                    case Key.S when shift:
                        return RemoteCommand.Stop;
                }
            }

            if (textEntry)
            {
                // Arrows, Enter and Backspace belong to the text box (Up/Down still move between fields).
                return key == Key.Enter ? RemoteCommand.Ok : RemoteCommand.None;
            }

            switch (key)
            {
                case Key.Left:
                    return RemoteCommand.Left;
                case Key.Right:
                    return RemoteCommand.Right;
                case Key.Enter:
                    return RemoteCommand.Ok;
                case Key.Back:
                    return RemoteCommand.Back;
                case Key.Home:
                    return alt ? RemoteCommand.Home : RemoteCommand.None;
                case Key.I:
                    return RemoteCommand.Info;
                case Key.G:
                    return RemoteCommand.Guide;
                case Key.Space:
                    return RemoteCommand.None;
            }

            if (!ctrl && !alt)
            {
                if (key >= Key.D0 && key <= Key.D9 && !shift)
                {
                    return RemoteCommand.Digit0 + (key - Key.D0);
                }

                if (key >= Key.NumPad0 && key <= Key.NumPad9)
                {
                    return RemoteCommand.Digit0 + (key - Key.NumPad0);
                }
            }

            return RemoteCommand.None;
        }

        /// <summary>
        /// Commands from WM_APPCOMMAND (lParam high word). Volume and mute are left to Windows
        /// so the system volume keeps working.
        /// </summary>
        public static RemoteCommand FromAppCommand(int command)
        {
            switch (command)
            {
                case 1: // APPCOMMAND_BROWSER_BACKWARD
                    return RemoteCommand.Back;
                case 7: // APPCOMMAND_BROWSER_HOME
                    return RemoteCommand.Home;
                case 13: // APPCOMMAND_MEDIA_STOP
                    return RemoteCommand.Stop;
                case 14: // APPCOMMAND_MEDIA_PLAY_PAUSE
                    return RemoteCommand.PlayPause;
                case 46: // APPCOMMAND_MEDIA_PLAY
                    return RemoteCommand.Play;
                case 47: // APPCOMMAND_MEDIA_PAUSE
                    return RemoteCommand.Pause;
                case 51: // APPCOMMAND_MEDIA_CHANNEL_UP
                    return RemoteCommand.ChannelUp;
                case 52: // APPCOMMAND_MEDIA_CHANNEL_DOWN
                    return RemoteCommand.ChannelDown;
                default:
                    return RemoteCommand.None;
            }
        }

        /// <summary>GET_APPCOMMAND_LPARAM.</summary>
        public static int GetAppCommand(long lParam)
        {
            return (short)((lParam >> 16) & 0xFFFF) & ~0xF000;
        }
    }
}
