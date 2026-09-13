# SwarmUI-RefMod

Applies [MiniMax H3 RefMods](https://github.com/Luisacaotica/ComfyUI-MiniMaxH3Mod).

This extension uses SwarmUI's existing embedding library and picker. It registers a **MiniMax H3 RefMod** architecture for safetensors files with the upstream `refmod_meta` header (or legacy `audio_refmod_meta`). This is distinct from SwarmUI's built-in **MiniMax H3 Embedding** architecture, which detects textual-inversion files with a `qwen3vl_32b` tensor.
