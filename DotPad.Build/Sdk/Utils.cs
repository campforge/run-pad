using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace DotPad.Build.Sdk;

internal class CmdResults : IEnumerable<string>, IEnumerable
{
	private IEnumerable<string> _input;

	public CmdResults(IEnumerable<string> input)
	{
		_input = input;
	}

	public IEnumerator<string> GetEnumerator()
	{
		return _input.GetEnumerator();
	}

	IEnumerator IEnumerable.GetEnumerator()
	{
		return GetEnumerator();
	}
}

[Serializable]
public class CommandExecutionException : Exception
{
	public int ExitCode { get; private set; }

	public string ErrorText { get; private set; }

	public CommandExecutionException(string msg)
		: base(msg)
	{
		ErrorText = msg;
	}

	public CommandExecutionException(string msg, int exitCode)
		: base(string.IsNullOrEmpty(msg) ? "The process returned an exit code of " + exitCode : msg)
	{
		ErrorText = msg;
		ExitCode = exitCode;
	}
}

public static class Utils
{

	public static IEnumerable<string> Shell(string commandText, string? args = null, Encoding? encoding = null)
	{
		IEnumerable<string> enumerable = ShellCore(commandText, args, encoding);

		enumerable = enumerable.ToArray();

		return new CmdResults(enumerable);
	}

	private static IEnumerable<string> ShellCore(string commandText, string? args, Encoding? encoding)
	{
		Process? process = null;

		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
			process = GetWindowsShellProcess(commandText, args, encoding);
		else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
			process = GetLinuxShellProcess(commandText, args, encoding);
		else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
			process = GetOSXShellProcess(commandText, args, encoding);

		if (process == null)
			yield break;

		using (process)
		{
			try
			{
				StringBuilder errors = new StringBuilder();
				var locker = new object();

				process.ErrorDataReceived += delegate (object sender, DataReceivedEventArgs errorArgs)
				{
					if (errorArgs.Data != null)
					{
						lock (locker)
						{
							errors.AppendLine(errorArgs.Data);
						}
					}
				};
				process.BeginErrorReadLine();
				while (true)
				{
					var s = process.StandardOutput.ReadLine();
					if (s == null)
					{
						break;
					}
					yield return s;
				}
				process.WaitForExit();
				int exitCode = process.ExitCode;
				if (exitCode != 0 && errors.Length > 0)
				{
					lock (locker)
					{
						throw new CommandExecutionException(errors.ToString(), exitCode);
					}
				}
			}
			finally
			{
				if (!process.HasExited)
				{
					try
					{
						process.Kill();
					}
					catch
					{
					}
				}
			}
		}
	}

	private static Process? GetLinuxShellProcess(string cmdText, string? args, Encoding? encoding)
	{
		cmdText = cmdText.Trim();

		var text = cmdText;
		if (text.Contains(' '))
		{
			text = "\"" + text + "\"";
		}

		var processStartInfo = new ProcessStartInfo
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			RedirectStandardInput = true,
			UseShellExecute = false,
			CreateNoWindow = true,
			StandardErrorEncoding = encoding ?? Encoding.UTF8,
			FileName = "/bin/bash"
		};

		if (encoding != null && encoding.EncodingName == "Unicode")
		{
			processStartInfo.Arguments = "/u /c " + text;
		}
		else
		{
			processStartInfo.Arguments = "/c " + text;
		}

		if (!string.IsNullOrEmpty(args))
		{
			processStartInfo.Arguments = processStartInfo.Arguments + " " + args;
		}
		try
		{
			if (Path.IsPathRooted(processStartInfo.FileName))
			{
				var directoryName = Path.GetDirectoryName(processStartInfo.FileName);
				if (Directory.Exists(directoryName))
				{
					processStartInfo.WorkingDirectory = directoryName;
				}
			}
		}
		catch (ArgumentException)
		{
		}

		return Process.Start(processStartInfo);
	}

	private static Process? GetOSXShellProcess(string cmdText, string? args, Encoding? encoding)
	{
		cmdText = cmdText.Trim();

		var text = cmdText;
		if (text.Contains(' '))
		{
			text = "\"" + text + "\"";
		}

		var processStartInfo = new ProcessStartInfo
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			RedirectStandardInput = true,
			UseShellExecute = false,
			CreateNoWindow = true,
			StandardErrorEncoding = encoding ?? Encoding.UTF8,
			FileName = "/bin/bash"
		};

		if (encoding != null && encoding.EncodingName == "Unicode")
		{
			processStartInfo.Arguments = "/u /c " + text;
		}
		else
		{
			processStartInfo.Arguments = "/c " + text;
		}

		if (!string.IsNullOrEmpty(args))
		{
			processStartInfo.Arguments = processStartInfo.Arguments + " " + args;
		}
		try
		{
			if (Path.IsPathRooted(processStartInfo.FileName))
			{
				var directoryName = Path.GetDirectoryName(processStartInfo.FileName);
				if (Directory.Exists(directoryName))
				{
					processStartInfo.WorkingDirectory = directoryName;
				}
			}
		}
		catch (ArgumentException)
		{
		}

		return Process.Start(processStartInfo);
	}

	private static Process? GetWindowsShellProcess(string cmdText, string? args, Encoding? encoding)
	{
		cmdText = cmdText.Trim();

		var text = cmdText;
		if (text.Contains(' '))
		{
			text = "\"" + text + "\"";
		}

		var processStartInfo = new ProcessStartInfo
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			RedirectStandardInput = true,
			UseShellExecute = false,
			CreateNoWindow = true,
			StandardErrorEncoding = encoding ?? Encoding.UTF8,
			FileName = "cmd.exe"
		};

		if (encoding != null && encoding.EncodingName == "Unicode")
		{
			processStartInfo.Arguments = "/u /c " + text;
		}
		else
		{
			processStartInfo.Arguments = "/c " + text;
		}
		if (!string.IsNullOrEmpty(args))
		{
			processStartInfo.Arguments = processStartInfo.Arguments + " " + args;
		}
		try
		{
			if (Path.IsPathRooted(processStartInfo.FileName))
			{
				var directoryName = Path.GetDirectoryName(processStartInfo.FileName);
				if (Directory.Exists(directoryName))
				{
					processStartInfo.WorkingDirectory = directoryName;
				}
			}
		}
		catch (ArgumentException)
		{
		}

		return Process.Start(processStartInfo);
	}
}

