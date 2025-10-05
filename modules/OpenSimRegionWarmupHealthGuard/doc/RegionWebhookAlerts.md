# RegionWebhookAlerts

## Configuration
~~~ini
[Modules]
    RegionWebhookAlerts = enabled

[RegionWebhookAlerts]
    ; Generic JSON webhook endpoint (e.g., n8n webhook URL)
    Url = https://your-n8n-host/webhook/abc123

    ; Minimal severity to send: Trace|Info|Warn|Error (default Warn)
    MinSeverity = Warn

    ; Collect incidents for N seconds and send in one POST
    BatchWindowSec = 10

    ; Max POSTs per minute (rate limit)
    RateLimitPerMin = 20

    ; Timeouts (ms)
    ConnectTimeoutMs = 5000
    SendTimeoutMs = 5000

    ; Which fields to include in payload.
    ; Available keys:
    ; ts,region,severity,message,metrics,agents,prims,scriptMs,physMs,netMs,scriptErrors,uptime
    PayloadFields = region,agents,prims,scriptMs,physMs,netMs,scriptErrors,uptime,severity,message,ts
~~~

## Notes

Unterstützt generisches JSON. Payload ist ein Array von Events.
Batching + Rate-Limit verhindern Spam. Für sofortige Zustellung BatchWindowSec verkleinern.
Abonniert Health-Events vom RegionHealthMonitor (IRegionHealthBus). Stelle sicher, dass dieser aktiviert ist.