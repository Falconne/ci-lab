---
applyTo: 'src/be/Mergician/Services/**'
---

- Only put services into the Gitlab dir if it is specifically dealing with Gitlab specific functionality. If it is core business logic for the Mergician app, then do not use the dedicated Gitlab dir. Take note that most of the core business logic of Mergician uses Gitlab, so consider the usage of the service before adding it to the Gitlab dir.