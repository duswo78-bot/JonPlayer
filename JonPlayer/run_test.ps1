$process = Start-Process -FilePath "dotnet" -ArgumentList "run" -WorkingDirectory "c:\Users\djw7ql\OneDrive - Aptiv\Antigravity\JonPlayer\JonPlayer" -RedirectStandardError "c:\Users\djw7ql\OneDrive - Aptiv\Antigravity\JonPlayer\JonPlayer\error.log" -RedirectStandardOutput "c:\Users\djw7ql\OneDrive - Aptiv\Antigravity\JonPlayer\JonPlayer\output.log" -PassThru
Start-Sleep -Seconds 5
if (!$process.HasExited) {
    Stop-Process -Id $process.Id -Force
}
