using System.Collections.ObjectModel;

namespace IntegratedContro.App;

/// <summary>Current content choices and the configuration version used when saving a camera mapping.</summary>
public interface IHiperwallContentLookup
{
    // Keep the collection instance stable and forward changes, including invalidation and session reset.
    ReadOnlyObservableCollection<HiperwallItemRow> Contents { get; }
    int ConfigurationVersion { get; }
}
