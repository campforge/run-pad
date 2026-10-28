using Avalonia.Media;
using RunPad.Contracts.IntelliSense;

namespace RunPad.Resources;

// Maps each CompletionItemKind to a small icon shown in the code editor's completion list
// (AvaloniaEdit's ICompletionData.Image), so properties/methods/enums/etc. are visually
// distinguishable at a glance instead of all looking identical. Geometry is Material Design Icons
// path data (https://pictogrammers.com/library/mdi/); colors are grouped by member "family"
// (callable/data/type/etc.), loosely following common IDE IntelliSense icon conventions.
public static class CompletionIcons
{
    private static readonly Dictionary<CompletionItemKind, IImage> _cache = new();

    public static IImage GetIcon(CompletionItemKind kind)
    {
        if (_cache.TryGetValue(kind, out var cached)) return cached;

        var (geometry, color) = GetGeometryAndColor(kind);
        var image = new DrawingImage(new GeometryDrawing
        {
            Geometry = Geometry.Parse(geometry),
            Brush = new SolidColorBrush(Color.Parse(color)),
        });
        _cache[kind] = image;
        return image;
    }

    private static (string Geometry, string Color) GetGeometryAndColor(CompletionItemKind kind) => kind switch
    {
        CompletionItemKind.Method or CompletionItemKind.ExtensionMethod => (AlphaMBoxOutline, Purple),
        CompletionItemKind.Function => (CodeBraces, Purple),
        CompletionItemKind.Constructor => (CubeOutline, Purple),

        CompletionItemKind.Field => (AlphaFBoxOutline, Blue),
        CompletionItemKind.Variable => (AlphaVBoxOutline, Blue),
        CompletionItemKind.Property => (AlphaPBoxOutline, Blue),
        CompletionItemKind.Constant => (LockOutline, Blue),

        CompletionItemKind.Class => (AlphaCBoxOutline, Orange),
        CompletionItemKind.Enum => (AlphaEBoxOutline, Orange),
        CompletionItemKind.EnumMember => (AlphaEBox, Orange),
        CompletionItemKind.Module => (ViewModule, Orange),
        CompletionItemKind.Delegate => (AlphaDBoxOutline, Orange),

        CompletionItemKind.Interface => (AlphaIBoxOutline, Teal),
        CompletionItemKind.Struct => (AlphaSBoxOutline, Teal),
        CompletionItemKind.TypeParameter => (AlphaTBoxOutline, Teal),

        CompletionItemKind.Event => (Flash, Gold),

        CompletionItemKind.Keyword => (KeyVariant, Gray),
        CompletionItemKind.Snippet => (CodeTags, Gray),
        CompletionItemKind.Operator => (PlusMinusVariant, Gray),
        CompletionItemKind.Reference => (Link, Gray),

        _ => (AlphabeticalVariant, Gray), // Text, and any future kind
    };

    private const string Purple = "#C586C0";
    private const string Blue = "#4FC1FF";
    private const string Orange = "#EE9D28";
    private const string Teal = "#4EC9B0";
    private const string Gold = "#EEC459";
    private const string Gray = "#9DA5B4";

    private const string AlphaMBoxOutline = "M9,7H15A2,2 0 0,1 17,9V17H15V9H13V16H11V9H9V17H7V9A2,2 0 0,1 9,7M5,3H19A2,2 0 0,1 21,5V19A2,2 0 0,1 19,21H5A2,2 0 0,1 3,19V5A2,2 0 0,1 5,3M5,5V19H19V5H5Z";
    private const string CodeBraces = "M8,3A2,2 0 0,0 6,5V9A2,2 0 0,1 4,11H3V13H4A2,2 0 0,1 6,15V19A2,2 0 0,0 8,21H10V19H8V14A2,2 0 0,0 6,12A2,2 0 0,0 8,10V5H10V3M16,3A2,2 0 0,1 18,5V9A2,2 0 0,0 20,11H21V13H20A2,2 0 0,0 18,15V19A2,2 0 0,1 16,21H14V19H16V14A2,2 0 0,1 18,12A2,2 0 0,1 16,10V5H14V3H16Z";
    private const string CubeOutline = "M21,16.5C21,16.88 20.79,17.21 20.47,17.38L12.57,21.82C12.41,21.94 12.21,22 12,22C11.79,22 11.59,21.94 11.43,21.82L3.53,17.38C3.21,17.21 3,16.88 3,16.5V7.5C3,7.12 3.21,6.79 3.53,6.62L11.43,2.18C11.59,2.06 11.79,2 12,2C12.21,2 12.41,2.06 12.57,2.18L20.47,6.62C20.79,6.79 21,7.12 21,7.5V16.5M12,4.15L6.04,7.5L12,10.85L17.96,7.5L12,4.15M5,15.91L11,19.29V12.58L5,9.21V15.91M19,15.91V9.21L13,12.58V19.29L19,15.91Z";
    private const string AlphaFBoxOutline = "M9,7H15V9H11V11H14V13H11V17H9V7M3,5A2,2 0 0,1 5,3H19A2,2 0 0,1 21,5V19A2,2 0 0,1 19,21H5C3.89,21 3,20.1 3,19V5M5,5V19H19V5H5Z";
    private const string AlphaVBoxOutline = "M9,7H11L12,12L13,7H15L13,17H11L9,7M5,3H19A2,2 0 0,1 21,5V19A2,2 0 0,1 19,21H5A2,2 0 0,1 3,19V5A2,2 0 0,1 5,3M5,5V19H19V5H5Z";
    private const string AlphaPBoxOutline = "M9,7H13A2,2 0 0,1 15,9V11A2,2 0 0,1 13,13H11V17H9V7M11,9V11H13V9H11M5,3H19A2,2 0 0,1 21,5V19A2,2 0 0,1 19,21H5A2,2 0 0,1 3,19V5A2,2 0 0,1 5,3M5,5V19H19V5H5Z";
    private const string LockOutline = "M12,17C10.89,17 10,16.1 10,15C10,13.89 10.89,13 12,13A2,2 0 0,1 14,15A2,2 0 0,1 12,17M18,20V10H6V20H18M18,8A2,2 0 0,1 20,10V20A2,2 0 0,1 18,22H6C4.89,22 4,21.1 4,20V10C4,8.89 4.89,8 6,8H7V6A5,5 0 0,1 12,1A5,5 0 0,1 17,6V8H18M12,3A3,3 0 0,0 9,6V8H15V6A3,3 0 0,0 12,3Z";
    private const string AlphaCBoxOutline = "M3,5A2,2 0 0,1 5,3H19A2,2 0 0,1 21,5V19A2,2 0 0,1 19,21H5C3.89,21 3,20.1 3,19V5M5,5V19H19V5H5M11,7H13A2,2 0 0,1 15,9V10H13V9H11V15H13V14H15V15A2,2 0 0,1 13,17H11A2,2 0 0,1 9,15V9A2,2 0 0,1 11,7Z";
    private const string AlphaDBoxOutline = "M9,7H13A2,2 0 0,1 15,9V15A2,2 0 0,1 13,17H9V7M11,9V15H13V9H11M5,3H19A2,2 0 0,1 21,5V19A2,2 0 0,1 19,21H5A2,2 0 0,1 3,19V5A2,2 0 0,1 5,3M5,5V19H19V5H5Z";
    private const string AlphaEBoxOutline = "M9,7H15V9H11V11H15V13H11V15H15V17H9V7M3,5A2,2 0 0,1 5,3H19A2,2 0 0,1 21,5V19A2,2 0 0,1 19,21H5C3.89,21 3,20.1 3,19V5M5,5V19H19V5H5Z";
    private const string AlphaEBox = "M9,7V17H15V15H11V13H15V11H11V9H15V7H9M5,3H19A2,2 0 0,1 21,5V19A2,2 0 0,1 19,21H5A2,2 0 0,1 3,19V5A2,2 0 0,1 5,3Z";
    private const string ViewModule = "M16,5V11H21V5M10,11H15V5H10M16,18H21V12H16M10,18H15V12H10M4,18H9V12H4M4,11H9V5H4V11Z";
    private const string AlphaIBoxOutline = "M14,7V9H13V15H14V17H10V15H11V9H10V7H14M5,3H19A2,2 0 0,1 21,5V19A2,2 0 0,1 19,21H5A2,2 0 0,1 3,19V5A2,2 0 0,1 5,3M5,5V19H19V5H5Z";
    private const string AlphaSBoxOutline = "M11,7H15V9H11V11H13A2,2 0 0,1 15,13V15A2,2 0 0,1 13,17H9V15H13V13H11A2,2 0 0,1 9,11V9A2,2 0 0,1 11,7M5,3H19A2,2 0 0,1 21,5V19A2,2 0 0,1 19,21H5A2,2 0 0,1 3,19V5A2,2 0 0,1 5,3M5,5V19H19V5H5Z";
    private const string AlphaTBoxOutline = "M9,7H15V9H13V17H11V9H9V7M5,3H19A2,2 0 0,1 21,5V19A2,2 0 0,1 19,21H5A2,2 0 0,1 3,19V5A2,2 0 0,1 5,3M5,5V19H19V5H5Z";
    private const string Flash = "M7,2V13H10V22L17,10H13L17,2H7Z";
    private const string KeyVariant = "M22,18V22H18V19H15V16H12L9.74,13.74C9.19,13.91 8.61,14 8,14A6,6 0 0,1 2,8A6,6 0 0,1 8,2A6,6 0 0,1 14,8C14,8.61 13.91,9.19 13.74,9.74L22,18M7,5A2,2 0 0,0 5,7A2,2 0 0,0 7,9A2,2 0 0,0 9,7A2,2 0 0,0 7,5Z";
    private const string CodeTags = "M14.6,16.6L19.2,12L14.6,7.4L16,6L22,12L16,18L14.6,16.6M9.4,16.6L4.8,12L9.4,7.4L8,6L2,12L8,18L9.4,16.6Z";
    private const string PlusMinusVariant = "M3 7H6V4H8V7H11V9H8V12H6V9H3V7M13 15H21V17H13V15M16.04 3H18.35L7.96 21H5.65L16.04 3Z";
    private const string Link = "M3.9,12C3.9,10.29 5.29,8.9 7,8.9H11V7H7A5,5 0 0,0 2,12A5,5 0 0,0 7,17H11V15.1H7C5.29,15.1 3.9,13.71 3.9,12M8,13H16V11H8V13M17,7H13V8.9H17C18.71,8.9 20.1,10.29 20.1,12C20.1,13.71 18.71,15.1 17,15.1H13V17H17A5,5 0 0,0 22,12A5,5 0 0,0 17,7Z";
    private const string AlphabeticalVariant = "M3 7A2 2 0 0 0 1 9V17H3V13H5V17H7V9A2 2 0 0 0 5 7H3M3 9H5V11H3M15 10.5V9A2 2 0 0 0 13 7H9V17H13A2 2 0 0 0 15 15V13.5A1.54 1.54 0 0 0 13.5 12A1.54 1.54 0 0 0 15 10.5M13 15H11V13H13V15M13 11H11V9H13M19 7A2 2 0 0 0 17 9V15A2 2 0 0 0 19 17H21A2 2 0 0 0 23 15V14H21V15H19V9H21V10H23V9A2 2 0 0 0 21 7Z";
}
