using System.Collections.ObjectModel;
using DotPad.Assets.Models;
using DotPad.Build;
using DotPad.Build.Core;
using DotPad.Build.Core.Models;
using DotPad.Shared;
using DotPad.UserControls.Models;
using DynamicData;
using ReactiveUI;

namespace DotPad.Windows.Models;

public class Person
{
    public string FirstName { get; set; }
    public string LastName { get; set; }
    public int AgeInYears { get; set; }

    public Person(string firstName, string lastName, int ageInYears)
    {
        FirstName = firstName;
        LastName = lastName;
        AgeInYears = ageInYears;
    }
}

public class DependenciesWindowViewModel : ViewModelBase
{
    public ObservableCollection<Person> People { get; }

    public DependenciesWindowViewModel()
    {
        var people = new List<Person>
            {
                 new Person("Neil", "Armstrong",  55),
                new Person("Buzz", "Lightyear", 38),
                new Person("James", "Kirk", 44)
            };
        People = new ObservableCollection<Person>(people);
    }
}
