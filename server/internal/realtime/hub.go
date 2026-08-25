package realtime

import (
	"encoding/json"
	"sync"
	"time"

	"github.com/gorilla/websocket"
)

type Envelope struct {
	Type  string `json:"type"`
	Data  any    `json:"data,omitempty"`
	Error string `json:"error,omitempty"`
}

type Hub struct {
	mu           sync.RWMutex
	connections  map[string]*client
	onDisconnect func(string)
}

type client struct {
	conn    *websocket.Conn
	writeMu sync.Mutex
}

func NewHub(onDisconnect func(string)) *Hub {
	return &Hub{connections: map[string]*client{}, onDisconnect: onDisconnect}
}

func (h *Hub) Attach(playerID string, conn *websocket.Conn) {
	h.mu.Lock()
	if old := h.connections[playerID]; old != nil {
		_ = old.conn.Close()
	}
	h.connections[playerID] = &client{conn: conn}
	h.mu.Unlock()
	conn.SetReadLimit(1 << 20)
	_ = conn.SetReadDeadline(time.Now().Add(15 * time.Second))
	conn.SetPongHandler(func(string) error { return conn.SetReadDeadline(time.Now().Add(15 * time.Second)) })
}

func (h *Hub) Detach(playerID string, conn *websocket.Conn) {
	h.mu.Lock()
	removed := false
	if current := h.connections[playerID]; current != nil && current.conn == conn {
		delete(h.connections, playerID)
		removed = true
	}
	h.mu.Unlock()
	if removed && h.onDisconnect != nil {
		h.onDisconnect(playerID)
	}
}

func (h *Hub) Send(playerID, eventType string, value any) error {
	h.mu.RLock()
	client := h.connections[playerID]
	h.mu.RUnlock()
	if client == nil {
		return nil
	}
	client.writeMu.Lock()
	defer client.writeMu.Unlock()
	_ = client.conn.SetWriteDeadline(time.Now().Add(5 * time.Second))
	return client.conn.WriteJSON(Envelope{Type: eventType, Data: value})
}

func (h *Hub) Broadcast(playerIDs []string, eventType string, value any) {
	for _, id := range playerIDs {
		_ = h.Send(id, eventType, value)
	}
}

func (h *Hub) IsConnected(playerID string) bool {
	h.mu.RLock()
	defer h.mu.RUnlock()
	return h.connections[playerID] != nil
}

func Decode(raw []byte, target any) (string, error) {
	var header struct {
		Type string          `json:"type"`
		Data json.RawMessage `json:"data"`
	}
	if err := json.Unmarshal(raw, &header); err != nil {
		return "", err
	}
	if target != nil && len(header.Data) > 0 {
		if err := json.Unmarshal(header.Data, target); err != nil {
			return "", err
		}
	}
	return header.Type, nil
}
