namespace DotPad.Resources;

internal class ResourceLoader
{
    internal static string LoadSampleFile(string fileName)
        => "Console.WriteLine(\"Hello, RPad!\");\n";

    internal static string LoadHtmlFile(string fileName)
    {
        return @"<div class=""widget"" color=""#fff"">
					  <h1>Widget Buttons</h1>
					  <button>A button element</button>
					  <input type=""submit"" value=""A submit button"">
 					  <a href=""#"">An anchor</a>
					</div>
					<h1>CSS Buttons</h1>
					<button class=""ui-button ui-widget ui-corner-all"">A button element</button>
 
					<input class=""ui-button ui-widget ui-corner-all"" type=""submit"" value=""A submit button"">
 					<a class=""ui-button ui-widget ui-corner-all"" href=""#"">An anchor</a>
  ";
    }
}
