FROM golang:1.25-alpine AS builder

ARG VERSION=0.1.0-dev

WORKDIR /build

COPY go.mod go.sum ./
RUN go mod download

COPY . .
RUN go build -ldflags="-s -w -X github.com/deneblab/stashlock-servergo/config.BuildVersion=${VERSION}" -o stashlock-servergo .

FROM alpine:3.20

RUN apk add --no-cache ca-certificates curl

WORKDIR /app

COPY --from=builder /build/stashlock-servergo /app/stashlock-servergo

ENV STASHLOCK_PORT=8080
ENV STASHLOCK_WORK_DIR=/app/work

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=5s --retries=3 \
    CMD curl -f http://localhost:8080/healthz || exit 1

ENTRYPOINT ["/app/stashlock-servergo"]
