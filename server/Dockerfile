FROM golang:1.27-alpine AS build
WORKDIR /src
COPY go.mod go.sum ./
RUN go mod download
COPY . .
RUN CGO_ENABLED=0 go build -trimpath -ldflags="-s -w" -o /out/spire-race-server ./cmd/server

FROM alpine:3.22
RUN apk add --no-cache ca-certificates tzdata \
    && addgroup -S spire-race \
    && adduser -S -G spire-race -h /app spire-race
WORKDIR /app
COPY --from=build /out/spire-race-server /app/spire-race-server
COPY migrations /app/migrations
COPY config /app/config
USER spire-race:spire-race
EXPOSE 8080
ENTRYPOINT ["/app/spire-race-server"]
