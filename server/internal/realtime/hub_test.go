package realtime

import (
	"testing"

	"github.com/gorilla/websocket"
)

func TestStaleConnectionDetachDoesNotDisconnectReplacement(t *testing.T) {
	disconnects := 0
	hub := NewHub(func(string) { disconnects++ })
	oldConn := &websocket.Conn{}
	currentConn := &websocket.Conn{}
	hub.connections["player"] = &client{conn: currentConn}

	hub.Detach("player", oldConn)

	if disconnects != 0 {
		t.Fatalf("stale connection triggered %d disconnect callbacks", disconnects)
	}
	if hub.connections["player"].conn != currentConn {
		t.Fatal("stale connection removed the active replacement")
	}
}

func TestCurrentConnectionDetachSignalsDisconnect(t *testing.T) {
	disconnects := 0
	hub := NewHub(func(string) { disconnects++ })
	conn := &websocket.Conn{}
	hub.connections["player"] = &client{conn: conn}

	hub.Detach("player", conn)

	if disconnects != 1 {
		t.Fatalf("current connection triggered %d disconnect callbacks", disconnects)
	}
	if hub.connections["player"] != nil {
		t.Fatal("current connection was not removed")
	}
}
