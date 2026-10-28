using System.Runtime.InteropServices;

namespace RunPad.Desktop.MacOS;

/// <summary>
/// Keeps AppKit from appending its own items — AutoFill, Start Dictation… and Emoji &amp; Symbols,
/// after a separator — to the menu bar's Edit menu, which then holds only RunPad's commands, as on
/// other systems. AppKit reads these switches from the user defaults when the application starts;
/// they are registered in the process's own registration domain, which is never written to disk, so
/// neither RunPad's preferences file nor any system-wide setting changes. Text input itself is
/// unaffected: dictation, the character viewer and AutoFill stay available from the system.
/// </summary>
internal static class MacOSEditMenuItems
{
    internal static readonly (string Key, bool Value)[] Defaults =
    [
        ("NSDisabledDictationMenuItem", true),
        ("NSDisabledCharacterPaletteMenuItem", true),
        ("NSAutoFillSystemInsertMenuEnabled", false),
    ];

    /// <summary>Call before Avalonia starts AppKit; does nothing on other systems.</summary>
    public static void Disable()
    {
        if (!OperatingSystem.IsMacOS()) return;

        var pool = objc_autoreleasePoolPush();
        try
        {
            var keys = new IntPtr[Defaults.Length];
            var values = new IntPtr[Defaults.Length];
            for (var i = 0; i < Defaults.Length; i++)
            {
                keys[i] = Send(Class("NSString"), Selector("stringWithUTF8String:"), Defaults[i].Key);
                values[i] = Send(Class("NSNumber"), Selector("numberWithBool:"), Defaults[i].Value);
            }

            var dictionary = Send(Class("NSDictionary"), Selector("dictionaryWithObjects:forKeys:count:"),
                values, keys, (nuint)Defaults.Length);
            Send(StandardUserDefaults(), Selector("registerDefaults:"), dictionary);
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }

    /// <summary>The value AppKit now reads for <paramref name="key"/> (test hook).</summary>
    internal static bool Read(string key)
    {
        var pool = objc_autoreleasePoolPush();
        try
        {
            var name = Send(Class("NSString"), Selector("stringWithUTF8String:"), key);
            return SendBool(StandardUserDefaults(), Selector("boolForKey:"), name);
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }

    private static IntPtr StandardUserDefaults() => Send(Class("NSUserDefaults"), Selector("standardUserDefaults"));

    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    [DllImport(ObjC, EntryPoint = "objc_getClass")]
    private static extern IntPtr Class([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(ObjC, EntryPoint = "sel_registerName")]
    private static extern IntPtr Selector([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(ObjC)]
    private static extern IntPtr objc_autoreleasePoolPush();

    [DllImport(ObjC)]
    private static extern void objc_autoreleasePoolPop(IntPtr pool);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr argument);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SendBool(IntPtr receiver, IntPtr selector, IntPtr argument);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.I1)] bool argument);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string argument);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr[] objects, IntPtr[] keys, nuint count);
}
