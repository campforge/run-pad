using System.IO;
using System.Reflection;

namespace AvaloniaEdit.Demo.Resources
{
	internal class ResourceLoader
	{

		internal static string LoadSampleFile(string fileName)
		{

			return @"  
using System;
using System.Threading;
namespace RoslynCompileSample
{
    public class Writer
    {
        public void Write(string message)
        {
			Thread.Sleep(1000);
            Console.WriteLine($""you said '{message}!'"");
        }
    }
}";
		}

		internal static string LoadHtmlFile(string fileName)
		{

			return @"<div class=""widget"">
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
}