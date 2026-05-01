#!/bin/bash
# Start headless Chrome for chrome-devtools-mcp
# Run this before starting opencode

CHROME_PORT=9222

# Kill any existing Chrome on this port
pkill -f "google-chrome.*--remote-debugging-port=$CHROME_PORT" 2>/dev/null
sleep 1

# Launch Chrome
google-chrome \
  --headless \
  --no-sandbox \
  --disable-gpu \
  --remote-debugging-port=$CHROME_PORT \
  --disable-extensions \
  > /dev/null 2>&1 &

echo "Chrome starting on port $CHROME_PORT..."

# Wait for Chrome to be ready
for i in {1..10}; do
  if curl -s "http://127.0.0.1:$CHROME_PORT/json/version" > /dev/null 2>&1; then
    echo "Chrome is ready!"
    exit 0
  fi
  sleep 1
done

echo "Error: Chrome failed to start"
exit 1
