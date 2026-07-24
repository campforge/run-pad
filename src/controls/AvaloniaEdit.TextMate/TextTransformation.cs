using System;
using System.Collections.Generic;

using AvaloniaEdit.Document;
using TextMateSharp.Themes;
using AM = Avalonia.Media;

namespace AvaloniaEdit.TextMate;

public abstract class TextTransformation : TextSegment
{
    public abstract void Transform(GenericLineTransformer transformer, DocumentLine line);
}

public class ForegroundTextTransformation : TextTransformation
{
    public Dictionary<int, AM.IBrush> ColorMap { get; set; }
    public Action<Exception> ExceptionHandler { get; set; }
    public int ForegroundColor { get; set; }
    public int BackgroundColor { get; set; }
    public FontStyle FontStyle { get; set; }

    public override void Transform(GenericLineTransformer transformer, DocumentLine line)
    {
        try
        {
            if (Length == 0)
            {
                return;
            }

            var formattedOffset = 0;
            var endOffset = line.EndOffset;

            if (StartOffset > line.Offset)
            {
                formattedOffset = StartOffset - line.Offset;
            }

            if (EndOffset < line.EndOffset)
            {
                endOffset = EndOffset;
            }

            transformer.SetTextStyle(line, formattedOffset, endOffset - line.Offset - formattedOffset,
                GetBrush(ForegroundColor),
                GetBrush(BackgroundColor),
                GetFontStyle(),
                GetFontWeight(),
                IsUnderline());
        }
        catch (Exception ex)
        {
            ExceptionHandler?.Invoke(ex);
        }
    }

    // TODO.md #112: bundled TextMateSharp themes (ThemeService.cs's ThemeName.* set, including the
    // default Dark+) mark the comment scope's tokenColors fontStyle as "italic" — the same convention
    // VS Code's own built-in themes use. Live user report: DotPad doesn't need to mirror that here —
    // a comment is already distinguished by color; italicizing it on top is unwanted noise, not a
    // deliberate readability choice. Always Normal regardless of what the active theme's fontStyle
    // says (internal, not private, so a test can exercise this directly without needing a full
    // TextView/DocumentLine to drive Transform()).
    internal AM.FontStyle GetFontStyle() => AM.FontStyle.Normal;

    AM.FontWeight GetFontWeight()
    {
        if (FontStyle != TextMateSharp.Themes.FontStyle.NotSet &&
            (FontStyle & TextMateSharp.Themes.FontStyle.Bold) != 0)
            return AM.FontWeight.Bold;

        return AM.FontWeight.Regular;
    }

    bool IsUnderline()
    {
        if (FontStyle != TextMateSharp.Themes.FontStyle.NotSet &&
            (FontStyle & TextMateSharp.Themes.FontStyle.Underline) != 0)
            return true;

        return false;
    }

    AM.IBrush GetBrush(int colorId)
    {
        if (ColorMap == null)
            return null;

        ColorMap.TryGetValue(colorId, out AM.IBrush result);
        return result;
    }
}
