# Changelog

All notable changes to this package will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Initial Unity package structure (`Runtime`, `Editor`, `Tests` assemblies).
- Backend: Unity Netcode for GameObjects 2.13.3.
- `NetworkSession`: host / dedicated server / client, command-line start, join/leave events.
- `NetworkCharacter`: server-authoritative `CharacterMotor` with client prediction, reconciliation and replay, interpolation for other players, owner-only components.
- 3d-network-schema C# models and `SessionChannel`: session document, numbered server messages, join/leave, state changes, game events and client requests, results, replay recording; `disconnectTimeout`, `mode`, display name (`-name`), max players on `NetworkSession`.
