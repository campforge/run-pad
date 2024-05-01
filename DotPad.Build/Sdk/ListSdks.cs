using System.Collections;
using System.Collections.Generic;
using System.Linq;
using DotPad.Build.Sdk;
using RoslynPad.Build.SDK.Models;

namespace RoslynPad.UI.SDK
{
    public class ListSdks : IEnumerable<SdkInfoModel>
    {
        private const string ListSdksCommand = "dotnet --list-sdks";
        private readonly List<SdkInfoModel> _list;
        public ListSdks()
        {
            _list = Init().ToList();
        }

        public IEnumerator<SdkInfoModel> GetEnumerator() => _list.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => _list.GetEnumerator();

        private IEnumerable<SdkInfoModel> Init()
        {
            var list = Utils.Shell(ListSdksCommand);

            foreach (var item in list)
            {
                string name = "";
                var line = item.Trim(' ');
                var spaseIndex = 0;

                for (; spaseIndex < line.Length; spaseIndex++)
                {
                    if (line[spaseIndex] == ' ')
                    {
                        name = line.Substring(0, spaseIndex);
                        break;
                    }
                }

                var startBrackedIndex = spaseIndex + 1;
                for (; startBrackedIndex < line.Length; startBrackedIndex++)
                {
                    if (line[startBrackedIndex] == '[')
                    {
                        break;
                    }
                }
                var endBrackedIndex = line.Length;
                for (; endBrackedIndex < line.Length; endBrackedIndex--)
                {
                    if (line[endBrackedIndex] == ']')
                    {
                        break;
                    }
                }

                var path = line.Substring(startBrackedIndex + 1, endBrackedIndex - startBrackedIndex - 2);

                yield return new SdkInfoModel(name, path);
            }

        }

    }
}
