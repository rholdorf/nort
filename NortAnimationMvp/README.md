# MonoGame Mixamo MVP

MVP de runtime animation layer com:

- organização de clips por nome (`idle`, `walk`, `run`, `bash`)
- `Animator` com playback + `CrossFade`
- pose em arrays contíguos (`Vector3[]`, `Quaternion[]`, `Vector3[]`)
- conversão local -> model/global -> skin palette
- GPU skinning com `SkinnedEffect`
- bone remap por submesh (`SkinnedMeshPart.BoneRemap`)
- textura difusa por material (FBX embutida/external, fallback branco)
- fundo `CornflowerBlue` e chão quadriculado (linhas 1m)

## Estrutura

- `Assets/FbxRuntimeLoader.cs`: importa FBX de modelo + FBX de animações
- `Runtime/*`: formato runtime-friendly (skeleton, meshpart, clips)
- `Animation/Animator.cs`: sample, blend, geração de skin matrices
- `Rendering/SkinnedMeshRenderer.cs`: upload da palette e draw
- `GameRoot.cs`: cena MVP e troca de animações no teclado

## Controles

- `1`: idle
- `2`: walk
- `3`: run
- `4`: bash
- `W/S`: frente/trás
- `A/D`: girar esquerda/direita (estilo tanque)
- `Q/E`: strafe lateral (esquerda/direita)
- `Mouse`: olhar (yaw/pitch)
- `Scroll`: zoom (FOV)
- `Shift`: mover câmera mais rápido
- `ESC`: sair

## Rodar

### Pré-requisito (macOS)

O projeto usa `AssimpNet` e no macOS é necessário ter o `assimp` instalado via Homebrew:

```bash
brew install assimp
```

O build copia automaticamente a lib do Homebrew para o output como `libassimp.dylib`, priorizando instalações atuais e tentando, nesta ordem:

- `/opt/homebrew/lib/libassimp.dylib`
- `/opt/homebrew/lib/libassimp.7.dylib`
- `/opt/homebrew/lib/libassimp.6.dylib`
- `/opt/homebrew/lib/libassimp.5.dylib`
- `/usr/local/lib/libassimp.dylib`
- `/usr/local/lib/libassimp.7.dylib`
- `/usr/local/lib/libassimp.6.dylib`
- `/usr/local/lib/libassimp.5.dylib`

Fallback legado (opcional), caso você ainda use fórmula versionada:

```bash
brew install assimp@5
```

Também é suportado:
- Apple Silicon: `/opt/homebrew/opt/assimp@5/lib/libassimp.5.dylib`
- Intel: `/usr/local/opt/assimp@5/lib/libassimp.5.dylib`

No diretório raiz:

```bash
dotnet build NortAnimationMvp.sln
dotnet run --project NortAnimationMvp/NortAnimationMvp.csproj
```

Requisito: ambiente com OpenGL/SDL2 funcional (janela gráfica).

## Culling do modelo

No arquivo `/Users/rui/src/pg/nort/NortAnimationMvp/GameRoot.cs`:

- `UseAutomaticModelCulling = true`: usa heurística do loader para escolher `CullClockwiseFace` ou `CullCounterClockwiseFace`.
- `UseAutomaticModelCulling = false`: usa o valor fixo de `ManualModelCullMode`.

Isso facilita manter o mesmo runtime para modelos com winding diferente.
