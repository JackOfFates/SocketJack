param(
    [string]$Endpoint = "http://127.0.0.1:11575/v1/chat/completions",
    [string]$Model = "",
    [string]$Prompt = "Reply with the word OK repeated twenty times, separated by spaces.",
    [int]$MaxTokens = 96,
    [int]$Runs = 3,
    [int]$TimeoutSeconds = 180,
    [string]$OutFile = ""
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Net.Http

function Read-ContentDelta {
    param([string]$DataLine)

    $text = ($DataLine -replace '^data:\s*', '').Trim()
    if ($text.Length -eq 0 -or $text -eq "[DONE]") {
        return ""
    }

    try {
        $json = $text | ConvertFrom-Json
        if ($json.choices -and $json.choices.Count -gt 0) {
            $delta = $json.choices[0].delta
            if ($delta -and $null -ne $delta.content) {
                return [string]$delta.content
            }
        }
    } catch {
        return ""
    }

    return ""
}

function Invoke-StreamingRun {
    param(
        [int]$RunNumber,
        [string]$Endpoint,
        [string]$Model,
        [string]$Prompt,
        [int]$MaxTokens,
        [int]$TimeoutSeconds
    )

    $messages = @(@{ role = "user"; content = $Prompt })
    $payload = @{
        stream = $true
        max_tokens = $MaxTokens
        messages = $messages
    }
    if ($Model.Trim().Length -gt 0) {
        $payload.model = $Model
    }

    $body = $payload | ConvertTo-Json -Depth 16
    $client = [System.Net.Http.HttpClient]::new()
    $client.Timeout = [TimeSpan]::FromSeconds([Math]::Max(1, $TimeoutSeconds))
    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Post, $Endpoint)
    $request.Headers.Accept.ParseAdd("text/event-stream")
    $request.Content = [System.Net.Http.StringContent]::new($body, [Text.Encoding]::UTF8, "application/json")

    $sw = [Diagnostics.Stopwatch]::StartNew()
    $headersMs = -1
    $firstByteMs = -1
    $firstFrameMs = -1
    $firstContentMs = -1
    $bytes = 0
    $frames = 0
    $contentChars = 0
    $statusChars = 0
    $visibleChars = 0
    $preview = ""
    $statusCode = 0
    $errorText = ""

    try {
        $response = $client.SendAsync($request, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
        $headersMs = [int]$sw.ElapsedMilliseconds
        $statusCode = [int]$response.StatusCode
        $stream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
        $buffer = New-Object byte[] 4096
        $lineBuffer = ""

        while ($sw.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
            $read = $stream.Read($buffer, 0, $buffer.Length)
            if ($read -le 0) {
                break
            }

            if ($firstByteMs -lt 0) {
                $firstByteMs = [int]$sw.ElapsedMilliseconds
            }

            $bytes += $read
            $chunk = [Text.Encoding]::UTF8.GetString($buffer, 0, $read)
            if ($preview.Length -lt 1200) {
                $preview += $chunk
            }

            $text = ($lineBuffer + $chunk).Replace("`r`n", "`n").Replace("`r", "`n")
            $lines = $text.Split("`n")
            $lineBuffer = $lines[$lines.Length - 1]
            for ($i = 0; $i -lt $lines.Length - 1; $i++) {
                $line = $lines[$i]
                if (-not $line.StartsWith("data:")) {
                    continue
                }

                $frames++
                if ($firstFrameMs -lt 0) {
                    $firstFrameMs = [int]$sw.ElapsedMilliseconds
                }

                $delta = Read-ContentDelta $line
                if ($delta.Length -gt 0) {
                    if ($firstContentMs -lt 0) {
                        $firstContentMs = [int]$sw.ElapsedMilliseconds
                    }
                    $visibleChars += $delta.Length
                    if ($delta.TrimStart().StartsWith("[SocketJack")) {
                        $statusChars += $delta.Length
                    } else {
                        $contentChars += $delta.Length
                    }
                }

                if ($line -match '\[DONE\]') {
                    break
                }
            }

            if ($chunk -match '\[DONE\]') {
                break
            }
        }

        $stream.Dispose()
        $response.Dispose()
    } catch {
        $errorText = $_.Exception.Message
    } finally {
        $sw.Stop()
        $request.Dispose()
        $client.Dispose()
    }

    $contentWindowSeconds = 0.0
    if ($firstContentMs -ge 0 -and $sw.ElapsedMilliseconds -gt $firstContentMs) {
        $contentWindowSeconds = ($sw.ElapsedMilliseconds - $firstContentMs) / 1000.0
    }

    $charsPerSecond = 0.0
    if ($contentWindowSeconds -gt 0) {
        $charsPerSecond = [Math]::Round($contentChars / $contentWindowSeconds, 2)
    }

    [pscustomobject]@{
        run = $RunNumber
        endpoint = $Endpoint
        statusCode = $statusCode
        headersMs = $headersMs
        firstByteMs = $firstByteMs
        firstFrameMs = $firstFrameMs
        firstContentMs = $firstContentMs
        totalMs = [int]$sw.ElapsedMilliseconds
        bytes = $bytes
        dataFrames = $frames
        contentChars = $contentChars
        statusChars = $statusChars
        visibleChars = $visibleChars
        contentCharsPerSecond = $charsPerSecond
        error = $errorText
        preview = (($preview -replace "`r|`n", " ").Trim())
    }
}

$results = @()
for ($i = 1; $i -le [Math]::Max(1, $Runs); $i++) {
    $result = Invoke-StreamingRun -RunNumber $i -Endpoint $Endpoint -Model $Model -Prompt $Prompt -MaxTokens $MaxTokens -TimeoutSeconds $TimeoutSeconds
    $results += $result
    $result | Select-Object run,statusCode,headersMs,firstByteMs,firstFrameMs,firstContentMs,totalMs,bytes,dataFrames,contentChars,contentCharsPerSecond,error | Format-Table -AutoSize
}

if ($OutFile.Trim().Length -gt 0) {
    $directory = Split-Path -Parent $OutFile
    if ($directory -and -not (Test-Path $directory)) {
        New-Item -ItemType Directory -Path $directory | Out-Null
    }
    $results | ConvertTo-Json -Depth 8 | Set-Content -Path $OutFile -Encoding UTF8
}

$results
