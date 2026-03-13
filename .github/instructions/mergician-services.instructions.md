---
applyTo: 'src/be/Mergician/Services/**'
---

- Only put services into the Gitlab dir if it is specifically dealing with Gitlab specific functionality. If it is core business logic for the Mergician app, then do not use the dedicated Gitlab dir. Take note that most of the core business logic of Mergician uses Gitlab, so consider the usage of the service before adding it to the Gitlab dir.
- Any background services created need to deal with Gitlab going down temporarily. Once the GitlAPI service's built in retries are exhausted, the background service needs to catch the exception and run its own retires indefinitely. If the background service is alreayd in a timed loop, there is no need for extra retries; just let the loop run with its existing delay to retry.