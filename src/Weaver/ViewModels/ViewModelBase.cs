using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;

namespace Weaver.ViewModels;

public class ViewModelBase : ObservableObject
{
    protected static T GetService<T>() where T : notnull
    {
        return App.Services!.GetRequiredService<T>();
    }
}
