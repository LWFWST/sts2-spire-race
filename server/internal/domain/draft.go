package domain

import (
	"crypto/rand"
	"errors"
	"math/big"
)

func AvailableCharacters(d LegendDraft, game int) []string {
	blocked := map[string]bool{d.PlayerOneBanOne: true, d.PlayerTwoBanOne: true}
	if game == 1 {
		blocked[d.PlayerOneBanTwo], blocked[d.PlayerTwoBanTwo] = true, true
	}
	used := map[string]bool{}
	for _, c := range d.UsedCharacters {
		used[c] = true
	}
	result := []string{}
	for _, c := range Characters {
		if !blocked[c] && !used[c] {
			result = append(result, c)
		}
	}
	return result
}

func SelectLegendCharacter(d LegendDraft, requested string) (string, error) {
	available := AvailableCharacters(d, d.GameNumber)
	for _, c := range available {
		if c == requested && d.GameNumber > 1 {
			return c, nil
		}
	}
	if requested != "" && d.GameNumber > 1 {
		return "", errors.New("character is not available")
	}
	if len(available) == 0 {
		return "", errors.New("no character remains")
	}
	i, err := rand.Int(rand.Reader, big.NewInt(int64(len(available))))
	if err != nil {
		return "", err
	}
	return available[i.Int64()], nil
}
