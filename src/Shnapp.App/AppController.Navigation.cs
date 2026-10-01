namespace Shnapp.App;

internal sealed partial class AppController
{
    private readonly record struct NavigationDestination(Guid? DocumentId);

    private readonly List<NavigationDestination> _navigationHistory = [new(null)];
    private int _navigationIndex;
    private bool _historyNavigationBusy;

    internal bool CanNavigateBack => !_historyNavigationBusy && _navigationIndex > 0;
    internal bool CanNavigateForward => !_historyNavigationBusy && _navigationIndex < _navigationHistory.Count - 1;

    internal void NavigateBack() => Run(() => NavigateHistoryAsync(-1));

    internal void NavigateForward() => Run(() => NavigateHistoryAsync(1));

    private void RecordNavigation(Guid? documentId)
    {
        var destination = new NavigationDestination(documentId);
        if (_navigationHistory[_navigationIndex] == destination)
        {
            UpdateNavigationButtons();
            return;
        }

        if (_navigationIndex + 1 < _navigationHistory.Count)
        {
            _navigationHistory.RemoveRange(_navigationIndex + 1, _navigationHistory.Count - _navigationIndex - 1);
        }

        _navigationHistory.Add(destination);
        _navigationIndex = _navigationHistory.Count - 1;
        UpdateNavigationButtons();
    }

    private async Task NavigateHistoryAsync(int direction)
    {
        if (_historyNavigationBusy || direction is not (-1 or 1))
        {
            return;
        }

        _historyNavigationBusy = true;
        UpdateNavigationButtons();
        try
        {
            while (_navigationIndex + direction >= 0 && _navigationIndex + direction < _navigationHistory.Count)
            {
                int targetIndex = _navigationIndex + direction;
                Guid? documentId = _navigationHistory[targetIndex].DocumentId;
                try
                {
                    if (documentId is Guid id)
                    {
                        await OpenDocumentAsync(id, recordHistory: false);
                    }
                    else
                    {
                        await OpenLibraryAsync(recordHistory: false);
                    }
                }
                catch (FileNotFoundException) when (documentId is not null)
                {
                    // An older entry may have been removed outside this running instance.
                    RemoveNavigationEntry(targetIndex);
                    CoalesceAdjacentDestinations();
                    continue;
                }

                bool reachedDestination = documentId is Guid openedId
                    ? _page.Document?.Id == openedId
                    : _page.Document is null;
                if (!reachedDestination)
                {
                    // A capture or another open action owns the navigation gate.
                    return;
                }

                _navigationIndex = targetIndex;
                return;
            }
        }
        finally
        {
            _historyNavigationBusy = false;
            UpdateNavigationButtons();
        }
    }

    private void RemoveNavigationEntry(int index)
    {
        _navigationHistory.RemoveAt(index);
        if (index <= _navigationIndex)
        {
            _navigationIndex--;
        }

        UpdateNavigationButtons();
    }

    // Called after a library delete so Back and Forward never point to deleted shnapps.
    private void ForgetNavigationDocuments(IEnumerable<Guid> documentIds)
    {
        var removed = documentIds.ToHashSet();
        for (int index = _navigationHistory.Count - 1; index > 0; index--)
        {
            if (_navigationHistory[index].DocumentId is Guid id && removed.Contains(id))
            {
                RemoveNavigationEntry(index);
            }
        }

        CoalesceAdjacentDestinations();
        UpdateNavigationButtons();
    }

    private void CoalesceAdjacentDestinations()
    {
        // Removing a document can join two visits to the Library. Keep one
        // destination so Back always changes what the user sees.
        for (int index = _navigationHistory.Count - 1; index > 0; index--)
        {
            if (_navigationHistory[index] == _navigationHistory[index - 1])
            {
                RemoveNavigationEntry(index);
            }
        }
    }

    private void UpdateNavigationButtons() =>
        _page.UpdateNavigationButtons(CanNavigateBack, CanNavigateForward);
}
