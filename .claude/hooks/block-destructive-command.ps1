$raw = [Console]::In.ReadToEnd()

if ([string]::IsNullOrWhiteSpace($raw)) {
    exit 0
}

try {
    $payload = $raw | ConvertFrom-Json -Depth 20
}
catch {
    exit 0
}

$command = $payload.tool_input.command
if ([string]::IsNullOrWhiteSpace($command)) {
    exit 0
}

$rules = @(
    @{ Regex = '(?i)\bgit\s+reset\s+--hard\b'; Reason = 'Blocked destructive command: git reset --hard discards working tree changes.' },
    @{ Regex = '(?i)\bgit\s+checkout\s+--\b'; Reason = 'Blocked destructive command: git checkout -- discards local file changes.' },
    @{ Regex = '(?i)\bgit\s+clean\b[^\r\n]*\-f[^\r\n]*\-d[^\r\n]*\-x'; Reason = 'Blocked destructive command: git clean -fdx removes untracked files recursively.' },
    @{ Regex = '(?i)(^|\s)rm\s+\-rf\b'; Reason = 'Blocked destructive command: rm -rf is not allowed without explicit manual intervention.' },
    @{ Regex = '(?i)Remove-Item\b[^\r\n]*\-Recurse[^\r\n]*\-Force'; Reason = 'Blocked destructive command: recursive forced deletion is not allowed by project policy.' },
    @{ Regex = '(?i)\bdel\b[^\r\n]*\/f[^\r\n]*\/s[^\r\n]*\/q'; Reason = 'Blocked destructive command: del /f /s /q performs forced recursive deletion.' }
)

foreach ($rule in $rules) {
    if ($command -match $rule.Regex) {
        [Console]::Error.WriteLine($rule.Reason)
        exit 2
    }
}

exit 0
