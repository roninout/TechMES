Remove-Item -Recurse -Force .vs -ErrorAction SilentlyContinue
Get-ChildItem -Recurse -Directory -Filter bin | Remove-Item -Recurse -Force
Get-ChildItem -Recurse -Directory -Filter obj | Remove-Item -Recurse -Force
Pause