# NginxAutoBan (NAB)

Monitors your NGINX log files for any matching patterns, and will ban the IPs sending those requests after some number of strikes.

## Install

```
> git clone https://github.com/tylercamp/NginxAutoBan
> cd NginxAutoBan
> dotnet publish -c Release -r linux-x64 -o publish
> cd publish
> nano appsettings.json
 ... (make your settings changes) ...
> chmod +x NginxAutoBan
> ./NginxAutoBan
```

### Default `appsettings.json` file:
```
{
  "Scanning": {
    "ViolationsThreshold": 5,
    "IpAddressPattern": "^(\\d+\\.\\d+\\.\\d+\\.\\d+)",
    "Patterns": [
      "\\.php HTTP"
    ]
  },

  "Nginx": {
    "LogFolder": "/var/log/nginx",
    "RulesFile": "/etc/nginx/autoblockips.conf"
  }
}
```

### Behavior
Edit the `appsettings.json` file and restart NAB to change the config. By default, it will:

1. Output all logs to `../Logs/` (`Serilog.MinimumLevel.Default`)
2. Watch all non-gz files in the NGINX log folder at `/var/log/nginx` for changes (`Nginx.LogFolder`)
3. Check each message for anything containing `".php HTTP"`, indicating a request for a PHP file (`Scanning.Patterns`)
4. Grab the source IP of the request with the regex `/^(\d+\.\d+\.\d+\.\d+)/` (`Scanning.IpAddressPattern`) and keep a record of a "strike" against that IP for making a malicious request
5. Collect strikes against an IP until at least 5 strikes were made (`Scanning.ViolationsThreshold`)
6. Modify an NGINX config file `/etc/nginx/autoblockips.conf` and append `deny <IP>;` (`Nginx.RulesFile`)
7. Reload NGINX config with `nginx -s reload` when a ban is added

(Your NGINX config will need to be updated to include the file at `/etc/nginx/autoblockips.conf`.)

This app requires at least permissions to:
- Create a folder and files for its own logs
- Enumerate the NGINX log directory and read all files
- Create and modify the given NGINX config file
- Run the command `nginx -s reload`



### Example `.service` unit file
```
[Unit]
Description=NGINX Auto-Ban

[Service]
WorkingDirectory=/NAB/PATH
ExecStart=/NAB/PATH/NginxAutoBan
Restart=always
RestartSec=10
SyslogIdentifier=nginx-autoban
User=root

Environment=Serilog__MinimumLevel=Information
Environment=Serilog__WriteTo__1__Args__pathFormat="../autoban-logs/log-{Date}.log"

Environment=Scanning__ViolationsThreshold=5
Environment=Scanning__IpAddressPattern="^(\\d+\\.\\d+\\.\\d+\\.\\d+)"

Environment=Scanning__Patterns__0="\\.php HTTP"
Environment=Scanning__Patterns__1="GET \\/RSeR HTTP"
Environment=Scanning__Patterns__2="\\/sdk\\/vimService HTTP"
Environment=Scanning__Patterns__3="GET \\/webdav\\/? HTTP"

Environment=Nginx__LogFolder=/var/log/nginx
Environment=Nginx__RulesFile=/etc/nginx/autoblockips.conf

[Install]
WantedBy=multi-user.target
```

### Example log output
```
2019-02-01 15:56:16.850 +00:00 [Information] Using scan pattern "\.php HTTP" as a regex
2019-02-01 15:56:16.884 +00:00 [Information] Using scan pattern "GET \/RSeR HTTP" as a regex
2019-02-01 15:56:16.884 +00:00 [Information] Successfully validated configuration.
2019-02-01 15:56:16.889 +00:00 [Debug] Reloading IP blacklist
2019-02-01 15:56:16.891 +00:00 [Debug] Got 5 IPs
2019-02-01 15:56:16.896 +00:00 [Debug] Getting existing log files...
2019-02-01 15:56:16.899 +00:00 [Debug] Found 4 existing files
2019-02-01 15:56:17.012 +00:00 [Debug] Blocking "198.167.223.52" for: "198.167.223.52 - - [31/Jan/2019:07:40:35 +0000] \"GET /acadmin.php HTTP/1.1\" 404 580 \"-\" \"Mozilla/5.0 (Windows NT 6.1; WOW64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/55.0.2883.87 Safari/537.36\" \"-\" \"172.100.197.141\" sn=\"v.tylercamp.me\" rt=0.000 ua=\"-\" us=\"-\" ut=\"-\" ul=\"-\" cs=-"
2019-02-01 15:56:17.016 +00:00 [Debug] 1 strike against "198.167.223.52" (currently at 1)
2019-02-01 15:56:17.129 +00:00 [Debug] Blocking "42.159.10.152" for: "42.159.10.152 - - [31/Jan/2019:20:31:08 +0000] \"GET /RSeR HTTP/1.1\" 404 580 \"-\" \"Mozilla/5.0 (compatible; MSIE 10.0; Windows NT 6.2; WOW64; Trident/6.0; Touch; MALCJS)\" \"-\" \"172.100.197.141\" sn=\"v.tylercamp.me\" rt=0.000 ua=\"-\" us=\"-\" ut=\"-\" ul=\"-\" cs=-"
2019-02-01 15:56:17.129 +00:00 [Debug] 1 strike against "42.159.10.152" (currently at 1)
2019-02-01 15:56:17.129 +00:00 [Debug] Blocking "42.159.10.152" for: "42.159.10.152 - - [31/Jan/2019:20:31:18 +0000] \"GET /RSeR HTTP/1.1\" 404 580 \"-\" \"Mozilla/5.0 (compatible; MSIE 10.0; Windows NT 6.2; WOW64; Trident/6.0; Touch; MALCJS)\" \"-\" \"172.100.197.141\" sn=\"v.tylercamp.me\" rt=0.000 ua=\"-\" us=\"-\" ut=\"-\" ul=\"-\" cs=-"
2019-02-01 15:56:17.133 +00:00 [Debug] 1 strike against "42.159.10.152" (currently at 2)
2019-02-01 15:56:17.133 +00:00 [Debug] Blocking "42.159.10.152" for: "42.159.10.152 - - [31/Jan/2019:20:31:28 +0000] \"GET /RSeR HTTP/1.1\" 404 580 \"-\" \"Mozilla/5.0 (compatible; MSIE 10.0; Windows NT 6.2; WOW64; Trident/6.0; Touch; MALCJS)\" \"-\" \"172.100.197.141\" sn=\"v.tylercamp.me\" rt=0.000 ua=\"-\" us=\"-\" ut=\"-\" ul=\"-\" cs=-"
2019-02-01 15:56:17.133 +00:00 [Debug] 1 strike against "42.159.10.152" (currently at 3)
2019-02-01 15:56:17.133 +00:00 [Debug] Blocking "42.159.10.152" for: "42.159.10.152 - - [31/Jan/2019:20:31:39 +0000] \"GET /RSeR HTTP/1.1\" 404 580 \"-\" \"Mozilla/5.0 (compatible; MSIE 10.0; Windows NT 6.2; WOW64; Trident/6.0; Touch; MALCJS)\" \"-\" \"172.100.197.141\" sn=\"v.tylercamp.me\" rt=0.000 ua=\"-\" us=\"-\" ut=\"-\" ul=\"-\" cs=-"
2019-02-01 15:56:17.133 +00:00 [Debug] 1 strike against "42.159.10.152" (currently at 4)
2019-02-01 15:56:17.133 +00:00 [Debug] Blocking "42.159.10.152" for: "42.159.10.152 - - [31/Jan/2019:20:31:49 +0000] \"GET /RSeR HTTP/1.1\" 404 580 \"-\" \"Mozilla/5.0 (compatible; MSIE 10.0; Windows NT 6.2; WOW64; Trident/6.0; Touch; MALCJS)\" \"-\" \"172.100.197.141\" sn=\"v.tylercamp.me\" rt=0.000 ua=\"-\" us=\"-\" ut=\"-\" ul=\"-\" cs=-"
2019-02-01 15:56:17.138 +00:00 [Information] Applying IP block on "42.159.10.152" for 5/5 strikes
2019-02-01 15:56:17.139 +00:00 [Information] Reloading nginx config
```
