using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Shnapp.App.ViewModels;

/// <summary>
/// Sample ViewModel using CommunityToolkit.Mvvm source generators.
/// Uses <see cref="ObservableProperty"/> for change notification and
/// <see cref="RelayCommand"/> for command binding.
/// </summary>
public partial class MainPageViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string Greeting { get; set; } = "Capture first. Think less.";

    [ObservableProperty]
    public partial int Counter { get; set; }

    [RelayCommand]
    private void Increment()
    {
        Counter++;
    }

    [RelayCommand]
    private void Decrement()
    {
        Counter--;
    }
}
