param(
    [Parameter(Mandatory=$true)]
    [string]$Endpoint,

    [ValidateSet("OpenAiSse", "JackLlmNdjson")]
    [string]$Format = "OpenAiSse",

    [string]$Model = "",
    [string]$Goal = "",
    [string]$Prompt = "Reply with exactly this sentence ten times: stream speed test.",
    [int]$MaxTokens = 96,
    [int]$Runs = 1,
    [int]$TimeoutSeconds = 180,
    [string]$OutFile = ""
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Net.Http

function Get-JsonContentText {
    param($Json)

    if ($null -eq $Json) {
        return ""
    }

    if ($Json.choices -and $Json.choices.Count -gt 0) {
        $choice = $Json.choices[0]
        if ($choice.delta -and $null -ne $choice.delta.content) {
            return [string]$choice.delta.content
        }
        if ($choice.message -and $null -ne $choice.message.content) {
            return [string]$choice.message.content
        }
    }

    if ($null -ne $Json.content) {
        return [string]$Json.content
    }

    return ""
}

function Get-JsonReasoningText {
    param($Json)

    if ($null -eq $Json) {
        return ""
    }

    if ($Json.choices -and $Json.choices.Count -gt 0) {
        $choice = $Json.choices[0]
        if ($choice.delta) {
            foreach ($name in @("reasoning", "reasoning_content", "thinking")) {
                if ($null -ne $choice.delta.$name) {
                    return [string]$choice.delta.$name
                }
            }
        }
    }

    if ($null -ne $Json.reasoning) {
        return [string]$Json.reasoning
    }

    return ""
}

function New-RequestBody {
    param(
        [string]$Format,
        [string]$Model,
        [string]$Prompt,
        [int]$MaxTokens
    )

    $messages = @(@{ role = "user"; content = $Prompt })
    if ($Format -eq "OpenAiSse") {
        $payload = @{
            stream = $true
            max_tokens = $MaxTokens
            messages = $messages
        }
    } else {
        $payload = @{
            max_tokens = $MaxTokens
            stream = $true
            sessionId = "bench_" + [Guid]::NewGuid().ToString("N")
            streamId = "bench_" + [Guid]::NewGuid().ToString("N")
            messages = $messages
        }
    }

    if ($Model.Trim().Length -gt 0) {
        $payload.model = $Model
    }

    return ($payload | ConvertTo-Json -Depth 32 -Compress)
}

function Read-OpenAiFrame {
    param([string]$Line)

    $text = ($Line -replace '^data:\s*', '').Trim()
    if ($text.Length -eq 0 -or $text -eq "[DONE]") {
        return [pscustomobject]@{ Done = ($text -eq "[DONE]"); Type = "data"; Content = ""; Reasoning = ""; Status = "" }
    }

    try {
        $json = $text | ConvertFrom-Json
        return [pscustomobject]@{
            Done = $false
            Type = "data"
            Content = Get-JsonContentText $json
            Reasoning = Get-JsonReasoningText $json
            Status = ""
        }
    } catch {
        return [pscustomobject]@{ Done = $false; Type = "parse_error"; Content = ""; Reasoning = ""; Status = $_.Exception.Message }
    }
}

function Read-JackLlmFrame {
    param([string]$Line)

    $trimmed = $Line.Trim()
    if ($trimmed.Length -eq 0) {
        return [pscustomobject]@{ Done = $false; Type = "blank"; Content = ""; Reasoning = ""; Status = "" }
    }

    try {
        $json = $trimmed | ConvertFrom-Json
        $type = [string]$json.type
        return [pscustomobject]@{
            Done = ($type -eq "done")
            Type = $type
            Content = if ($null -ne $json.content) { [string]$json.content } else { "" }
            Reasoning = if ($null -ne $json.reasoning) { [string]$json.reasoning } else { "" }
            Status = if ($null -ne $json.status) { [string]$json.status } else { "" }
        }
    } catch {
        return [pscustomobject]@{ Done = $false; Type = "parse_error"; Content = ""; Reasoning = ""; Status = $_.Exception.Message }
    }
}

function Invoke-StreamingRun {
    param(
        [int]$RunNumber,
        [string]$Endpoint,
        [string]$Format,
        [string]$Model,
        [string]$Goal,
        [string]$Prompt,
        [int]$MaxTokens,
        [int]$TimeoutSeconds
    )

    $body = New-RequestBody -Format $Format -Model $Model -Prompt $Prompt -MaxTokens $MaxTokens
    $client = [System.Net.Http.HttpClient]::new()
    $client.Timeout = [TimeSpan]::FromSeconds([Math]::Max(1, $TimeoutSeconds))
    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Post, $Endpoint)
    if ($Format -eq "OpenAiSse") {
        $request.Headers.Accept.ParseAdd("text/event-stream")
    } else {
        $request.Headers.Accept.ParseAdd("application/x-ndjson")
    }
    $request.Content = [System.Net.Http.StringContent]::new($body, [Text.Encoding]::UTF8, "application/json")

    $sw = [Diagnostics.Stopwatch]::StartNew()
    $headersMs = -1
    $firstByteMs = -1
    $firstFrameMs = -1
    $firstContentMs = -1
    $bytes = 0
    $frames = 0
    $contentFrames = 0
    $usageFrames = 0
    $progressFrames = 0
    $doneFrames = 0
    $contentChars = 0
    $reasoningChars = 0
    $statusChars = 0
    $preview = ""
    $statusCode = 0
    $errorText = ""
    $contentDone = $false
    $lineBuffer = ""

    try {
        $response = $client.SendAsync($request, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
        $headersMs = [int]$sw.ElapsedMilliseconds
        $statusCode = [int]$response.StatusCode
        $stream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
        $buffer = New-Object byte[] 8192

        while (-not $contentDone -and $sw.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
            $read = $stream.Read($buffer, 0, $buffer.Length)
            if ($read -le 0) {
                break
            }

            if ($firstByteMs -lt 0) {
                $firstByteMs = [int]$sw.ElapsedMilliseconds
            }

            $bytes += $read
            $chunk = [Text.Encoding]::UTF8.GetString($buffer, 0, $read)
            if ($preview.Length -lt 1600) {
                $preview += $chunk
            }

            $text = ($lineBuffer + $chunk).Replace("`r`n", "`n").Replace("`r", "`n")
            $lines = $text.Split("`n")
            $lineBuffer = $lines[$lines.Length - 1]

            for ($i = 0; $i -lt $lines.Length - 1; $i++) {
                $line = $lines[$i]
                if ($Format -eq "OpenAiSse" -and -not $line.StartsWith("data:")) {
                    continue
                }

                $frame = if ($Format -eq "OpenAiSse") { Read-OpenAiFrame $line } else { Read-JackLlmFrame $line }
                if ($frame.Type -eq "blank") {
                    continue
                }

                $frames++
                if ($firstFrameMs -lt 0) {
                    $firstFrameMs = [int]$sw.ElapsedMilliseconds
                }

                if ($frame.Type -eq "usage") {
                    $usageFrames++
                } elseif ($frame.Type -eq "progress") {
                    $progressFrames++
                } elseif ($frame.Type -eq "done") {
                    $doneFrames++
                }

                $deltaChars = ($frame.Content.Length + $frame.Reasoning.Length)
                if ($deltaChars -gt 0) {
                    $contentFrames++
                    if ($firstContentMs -lt 0) {
                        $firstContentMs = [int]$sw.ElapsedMilliseconds
                    }
                    $contentChars += $frame.Content.Length
                    $reasoningChars += $frame.Reasoning.Length
                }
                if ($frame.Status.Length -gt 0) {
                    $statusChars += $frame.Status.Length
                }

                if ($frame.Done) {
                    $contentDone = $true
                    break
                }
            }
        }

        if (-not $contentDone -and $lineBuffer.Trim().Length -gt 0 -and $Format -eq "JackLlmNdjson") {
            $frame = Read-JackLlmFrame $lineBuffer
            if ($frame.Type -ne "blank") {
                $frames++
                if ($frame.Done) { $doneFrames++ }
                if ($frame.Content.Length + $frame.Reasoning.Length -gt 0) {
                    $contentFrames++
                    if ($firstContentMs -lt 0) { $firstContentMs = [int]$sw.ElapsedMilliseconds }
                    $contentChars += $frame.Content.Length
                    $reasoningChars += $frame.Reasoning.Length
                }
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
        $charsPerSecond = [Math]::Round(($contentChars + $reasoningChars) / $contentWindowSeconds, 2)
    }

    [pscustomobject]@{
        run = $RunNumber
        goal = $Goal
        endpoint = $Endpoint
        format = $Format
        model = $Model
        maxTokens = $MaxTokens
        statusCode = $statusCode
        headersMs = $headersMs
        firstByteMs = $firstByteMs
        firstFrameMs = $firstFrameMs
        firstContentMs = $firstContentMs
        totalMs = [int]$sw.ElapsedMilliseconds
        bytes = $bytes
        frames = $frames
        contentFrames = $contentFrames
        usageFrames = $usageFrames
        progressFrames = $progressFrames
        doneFrames = $doneFrames
        contentChars = $contentChars
        reasoningChars = $reasoningChars
        statusChars = $statusChars
        charsPerSecond = $charsPerSecond
        error = $errorText
        preview = (($preview -replace "`r|`n", " ").Trim())
    }
}

$results = @()
for ($i = 1; $i -le [Math]::Max(1, $Runs); $i++) {
    $result = Invoke-StreamingRun -RunNumber $i -Endpoint $Endpoint -Format $Format -Model $Model -Goal $Goal -Prompt $Prompt -MaxTokens $MaxTokens -TimeoutSeconds $TimeoutSeconds
    $results += $result
    $result | Select-Object run,statusCode,headersMs,firstByteMs,firstFrameMs,firstContentMs,totalMs,bytes,frames,contentFrames,usageFrames,progressFrames,contentChars,reasoningChars,charsPerSecond,error | Format-Table -AutoSize
}

if ($OutFile.Trim().Length -gt 0) {
    $directory = Split-Path -Parent $OutFile
    if ($directory -and -not (Test-Path $directory)) {
        New-Item -ItemType Directory -Path $directory | Out-Null
    }
    $results | ConvertTo-Json -Depth 8 | Set-Content -Path $OutFile -Encoding UTF8
}

$results | ConvertTo-Json -Depth 8
