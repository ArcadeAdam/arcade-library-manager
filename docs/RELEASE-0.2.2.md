# Version 0.2.2

Fixes LaunchBox XML serialization after a synchronization. Appending games to a whitespace-preserving document could produce compact adjacent records and explicit empty text elements, despite requesting indentation. A generic XML parse accepted that output, but the installed LaunchBox reader reported an invalid XmlNodeType at startup.

Platform commits now use consistent indented records and self-closing empty values, while preserving existing game identities, metadata, alternate applications, comments, and unknown fields. Focused regression checks cover adding to an existing formatted document and preserving meaningful multiline text. Existing backup, hash, process, and volume guards remain in place.

Use this version for subsequent LaunchBox synchronizations. Previously written files can be recovered from their exact app-created backup or reformatted with verified semantic preservation; game payloads are unaffected.