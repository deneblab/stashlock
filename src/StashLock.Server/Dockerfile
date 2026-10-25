FROM golang:1.27-alpine AS builder

ARG VERSION=0.1.0-dev

WORKDIR /build

COPY go.mod go.sum ./
RUN go mod download

COPY . .
RUN CGO_ENABLED=0 go build -ldflags="-s -w -X github.com/deneblab/stashlock-servergo/config.BuildVersion=${VERSION}" -o stashlock-servergo .

FROM alpine:3.24

RUN apk add --no-cache ca-certificates \
    && addgroup -S -g 10001 stashlock \
    && adduser -S -D -H -u 10001 -G stashlock stashlock \
    && mkdir -p /app/work /app/log \
    && chown -R stashlock:stashlock /app

WORKDIR /app

COPY --from=builder /build/stashlock-servergo /app/stashlock-servergo

ENV STASHLOCK_PORT=8080
ENV STASHLOCK_WORK_DIR=/app/work
ENV STASHLOCK_LOG_DIR=/app/log

# Runs as an unprivileged user; bind-mounted ./app/work and ./app/log must be writable by uid 10001
USER 10001:10001

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=5s --retries=3 \
    CMD ["/app/stashlock-servergo", "healthcheck"]

ENTRYPOINT ["/app/stashlock-servergo"]
