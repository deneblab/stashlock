package logging

import (
	"io"
	"os"
	"path/filepath"
	"strings"

	"github.com/rs/zerolog"
	"github.com/rs/zerolog/log"
	"gopkg.in/lumberjack.v2"
)

// Setup initializes zerolog with dual output: console (stderr) + plain text file.
// logDir is the directory for the log file. logLevel is one of: debug, info, warn, error.
func Setup(logDir string, logLevel string) {
	// Parse log level
	level := parseLevel(logLevel)
	zerolog.SetGlobalLevel(level)

	// Console writer (human-friendly, colored)
	consoleWriter := zerolog.ConsoleWriter{
		Out:        os.Stderr,
		TimeFormat: "2006-01-02T15:04:05",
	}

	// File writer with rotation (plain text, same format as console)
	if err := os.MkdirAll(logDir, 0755); err != nil {
		log.Error().Err(err).Str("logDir", logDir).Msg("Failed to create log directory, file logging disabled")
		log.Logger = zerolog.New(consoleWriter).With().Timestamp().Logger()
		return
	}

	fileRotator := &lumberjack.Logger{
		Filename:   filepath.Join(logDir, "stashlock-servergo.log"),
		MaxSize:    10, // megabytes
		MaxBackups: 3,
		LocalTime:  true,
	}

	fileWriter := zerolog.ConsoleWriter{
		Out:        fileRotator,
		NoColor:    true,
		TimeFormat: "2006-01-02T15:04:05",
	}

	// Dual output: console + file
	multi := io.MultiWriter(consoleWriter, fileWriter)
	log.Logger = zerolog.New(multi).With().Timestamp().Logger()
}

func parseLevel(s string) zerolog.Level {
	switch strings.ToLower(strings.TrimSpace(s)) {
	case "debug", "dbg":
		return zerolog.DebugLevel
	case "warn", "warning", "wrn":
		return zerolog.WarnLevel
	case "error", "err":
		return zerolog.ErrorLevel
	case "trace", "trc":
		return zerolog.TraceLevel
	default:
		return zerolog.InfoLevel
	}
}
