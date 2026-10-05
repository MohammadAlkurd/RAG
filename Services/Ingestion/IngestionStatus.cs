namespace RAG.Services.Ingestion;


public class IngestionStatus
{ 
    private volatile IngestionSnapshot _current = new(IngestionState.Idle);
    
    public IngestionSnapshot Current => _current;
    
    public bool IsBusy => _current.State is not (IngestionState.Idle or IngestionState.Ready or IngestionState.Failed);
    
    public void Set(IngestionState state, string? progress = null) =>
        _current = _current with { State = state, Progress = progress };

    public void Start(string fileName) =>
        _current = new IngestionSnapshot(IngestionState.Queued, fileName);

    public void Fail(string error) =>
        _current = _current with { State = IngestionState.Failed, Error = error };
}