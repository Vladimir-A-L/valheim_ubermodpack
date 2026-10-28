using System;
using System.Runtime.InteropServices;

public static class ValheimInput {
    [StructLayout(LayoutKind.Sequential)] struct Mouse {
        public int x, y; public uint data, flags, time; public UIntPtr extra;
    }
    [StructLayout(LayoutKind.Sequential)] struct Keyboard {
        public ushort key, scan; public uint flags, time; public UIntPtr extra;
    }
    [StructLayout(LayoutKind.Explicit)] struct Union {
        [FieldOffset(0)] public Mouse mouse;
        [FieldOffset(0)] public Keyboard keyboard;
    }
    [StructLayout(LayoutKind.Sequential)] struct Input { public uint type; public Union value; }
    [DllImport("user32.dll", SetLastError=true)] static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint mapType);
    [StructLayout(LayoutKind.Sequential)] struct LastInput { public uint size, time; }
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LastInput input);
    public static uint IdleMilliseconds() {
        var last = new LastInput { size = (uint)Marshal.SizeOf(typeof(LastInput)) };
        if (!GetLastInputInfo(ref last)) throw new Exception("last_input_unavailable");
        return unchecked((uint)Environment.TickCount - last.time);
    }
    static void Send(Input input) {
        if (SendInput(1, new[] {input}, Marshal.SizeOf(typeof(Input))) != 1)
            throw new Exception("sendinput_failed");
    }
    public static void Key(ushort code, bool up, bool unicode) {
        // Unity читает физические scan codes для игровых клавиш; текст приходит как Unicode.
        ushort scan = unicode ? code : (ushort)MapVirtualKey(code, 0);
        if (!unicode && scan == 0) throw new Exception("scan_code_unavailable");
        uint extended = !unicode && code >= 37 && code <= 40 ? 1u : 0u;
        var key = new Keyboard { key = 0, scan = scan,
            flags = (up ? 2u : 0u) | (unicode ? 4u : 8u) | extended };
        Send(new Input { type = 1, value = new Union { keyboard = key } });
    }
    public static void Button(bool up) {
        Send(new Input { type = 0, value = new Union { mouse = new Mouse { flags = up ? 4u : 2u } } });
    }
    public static void Look(int dx, int dy) {
        Send(new Input { type = 0, value = new Union { mouse = new Mouse { x = dx, y = dy, flags = 1u } } });
    }
}
