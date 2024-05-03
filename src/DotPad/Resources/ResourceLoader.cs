namespace DotPad.Resources;

internal class ResourceLoader
{
    internal static string LoadSampleFile(string fileName)
    {
        return @"
using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using static ObjectExtensions;

namespace UserQuery {
	
public class Program
{
    public static void Main()
    {
        Debugger.Break();
		for (int j = 0; j < 5; j++)
		{
			Console.WriteLine($""you said {j}"");
			Thread.Sleep(1000);
		}
        var i = new[] {100, 200, 100};
		i.Dump();
		AppDomain.CurrentDomain.BaseDirectory.Dump();
    }
}}
";
    }

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