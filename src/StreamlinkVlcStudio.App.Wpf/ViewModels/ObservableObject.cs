using StreamlinkVlcStudio.Core.Settings;

namespace StreamlinkVlcStudio.App.Wpf.ViewModels;

/// <summary>
/// Base class for the WPF view models. The change-notification plumbing lives in Core's
/// <see cref="NotifyPropertyChangedObject"/> so the settings models and the view models cannot
/// drift apart; this type only keeps the name the view models are declared against.
/// </summary>
public abstract class ObservableObject : NotifyPropertyChangedObject;
