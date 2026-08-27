package httpapi

import "testing"

func TestReplayBacklogIsOrderedAndChunked(t *testing.T) {
	server := &Server{replayStreams: map[string]*replayLiveStream{}}
	watch := replayWatch{MatchID: "match", GameID: "game", PlayerID: "source"}
	stream := &replayLiveStream{
		Latest: replayLiveBatch{MatchID: watch.MatchID, GameID: watch.GameID, PlayerID: watch.PlayerID, EventCount: 130},
		Events: map[int]replayLiveEvent{},
	}
	for index := 129; index >= 0; index-- {
		stream.Events[index] = replayLiveEvent{Index: index, Kind: "native"}
	}
	server.replayStreams[replayStreamKey(watch.MatchID, watch.GameID, watch.PlayerID)] = stream

	batches := server.replayBacklogLocked(watch)
	if len(batches) != 4 {
		t.Fatalf("expected three event chunks and a state batch, got %d", len(batches))
	}
	wantIndex := 0
	for _, batch := range batches[:len(batches)-1] {
		for _, event := range batch.Events {
			if event.Index != wantIndex {
				t.Fatalf("event order mismatch: got %d, want %d", event.Index, wantIndex)
			}
			wantIndex++
		}
		if batch.EventCount != wantIndex {
			t.Fatalf("chunk cursor mismatch: got %d, want %d", batch.EventCount, wantIndex)
		}
	}
	if wantIndex != 130 || len(batches[len(batches)-1].Events) != 0 || batches[len(batches)-1].EventCount != 130 {
		t.Fatalf("incomplete backlog: events=%d final=%+v", wantIndex, batches[len(batches)-1])
	}
}

func TestReplayBacklogMissingStream(t *testing.T) {
	server := &Server{replayStreams: map[string]*replayLiveStream{}}
	if batches := server.replayBacklogLocked(replayWatch{MatchID: "missing", GameID: "g", PlayerID: "p"}); batches != nil {
		t.Fatalf("expected no backlog, got %+v", batches)
	}
}
