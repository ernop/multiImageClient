using System;
using System.Runtime.InteropServices;

namespace MultiImageClient
{
    /// Windows-only helper that stuffs text into the console input buffer so
    /// a subsequent <see cref="Console.ReadLine"/> call appears pre-populated
    /// and editable, exactly as if the user had just typed the characters
    /// themselves. Used by the REPL's `:random` command to offer a suggested
    /// prompt that the user can tweak in place before hitting Enter.
    ///
    /// The technique is WriteConsoleInputW with a synthesized KEY_DOWN event
    /// per character (UnicodeChar set, virtual key codes zeroed since cooked
    /// line input only reads the character). If stdin isn't a console (e.g.
    /// redirected from a file) we silently no-op; callers should still print
    /// the suggested text in that case so piped usage stays sensible.
    internal static class ConsolePrefill
    {
        private const int STD_INPUT_HANDLE = -10;

        [StructLayout(LayoutKind.Sequential)]
        private struct KEY_EVENT_RECORD
        {
            public int bKeyDown; // BOOL
            public ushort wRepeatCount;
            public ushort wVirtualKeyCode;
            public ushort wVirtualScanCode;
            public char UnicodeChar;
            public uint dwControlKeyState;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct INPUT_RECORD
        {
            [FieldOffset(0)] public ushort EventType;
            // KEY_EVENT_RECORD starts 4 bytes in to match the native union
            // padding; the rest of the union (mouse/window/etc.) is unused.
            [FieldOffset(4)] public KEY_EVENT_RECORD KeyEvent;
        }

        private const ushort KEY_EVENT = 0x0001;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "WriteConsoleInputW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WriteConsoleInputW(
            IntPtr hConsoleInput,
            [MarshalAs(UnmanagedType.LPArray), In] INPUT_RECORD[] lpBuffer,
            uint nLength,
            out uint lpNumberOfEventsWritten);

        /// Push <paramref name="text"/> into stdin as synthesized key events.
        /// Returns true on success, false if stdin is redirected or the
        /// Win32 call failed — callers should then fall back to prompting.
        public static bool TryPrefill(string text)
        {
            if (string.IsNullOrEmpty(text)) return true;
            if (Console.IsInputRedirected) return false;

            var handle = GetStdHandle(STD_INPUT_HANDLE);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return false;

            var records = new INPUT_RECORD[text.Length];
            for (int i = 0; i < text.Length; i++)
            {
                records[i] = new INPUT_RECORD
                {
                    EventType = KEY_EVENT,
                    KeyEvent = new KEY_EVENT_RECORD
                    {
                        bKeyDown = 1,
                        wRepeatCount = 1,
                        wVirtualKeyCode = 0,
                        wVirtualScanCode = 0,
                        UnicodeChar = text[i],
                        dwControlKeyState = 0,
                    }
                };
            }

            return WriteConsoleInputW(handle, records, (uint)records.Length, out _);
        }
    }
}
