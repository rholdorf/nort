# Instructions

dentro do diretório mixamo_models há os seguintes arquivos:

- mixamo_models/bash.fbx
- mixamo_models/idle.fbx
- mixamo_models/run.fbx
- mixamo_models/walk.fbx
- mixamo_models/y-bot.fbx

Quero montar um runtime animation layer no MonoGame, cobrindo: organization of animation clips, GPU skinning, layout cache-friendly para bones. Neste momento, um MVP simples, que consiga carregar o modelo mixamo_models/y-bot.fbx, e alternar entre as animações bash, idle, run, walk. Os arquivos foram baixados do site mixamo.com.

## Objetivo e arquitetura mínima

Você quer separar claramente:

- Asset pipeline (offline): importa FBX/GLTF e gera um asset runtime-friendly (seu formato).
- Runtime animation layer: avalia animações, produz pose, faz blending, e entrega dados para render.
- Renderer: desenha com GPU skinning.

## Organização de clips e “state machine” de animação

Mínimo funcional:

- Animator por entidade:
- estado atual (clip, tempo, speed)
- lista de layers (base + upper body, por ex.)
- transições com blend time
- (opcional) máscaras por bone (camada afeta apenas parte do corpo)

Modelo simples:

- Base layer: locomotion (idle/walk/run)
- Additive layer (opcional): recoil, breathing
- Upper body layer (opcional): aim, reload (com máscara)

## Avaliação de animação: pose local → pose de skin

### Avaliar (sample) em local space

Você quer produzir, para cada bone, um Transform local:

- T: Vector3
- R: Quaternion
- S: Vector3

Sampler por track:

- encontra os 2 keyframes vizinhos (índices k0, k1)
- alpha = (t - time[k0]) / (time[k1] - time[k0])
- interpola:
- T e S: lerp
- R: slerp (ou nlerp + normalize, dependendo da qualidade)

### Blending entre clips

Blending padrão:

- T: lerp
- S: lerp
- R: slerp/nlerp

Quando tiver camadas com máscara:

- se bone mascarado: aplica blend
- senão: mantém pose da base

### Converter para model space (também chamado de global pose)

Loop topológico (pais antes dos filhos):

- modelPose[root] = localToMatrix(local[root])
- modelPose[i] = modelPose[parent[i]] * localToMatrix(local[i])

### Skin matrices (matriz final por bone)

A clássica:

- skin[i] = modelPose[i] * invBind[i]

Aqui nasce o gargalo (muito Matrix4x4 por frame).

## Como evitar o gargalo de matrizes por frame

O gargalo comum é uma mistura de:
	1.	custo CPU em multiplicações de Matrix4x4
	2.	custo de upload de muitas matrizes para shader
	3.	cache misses por estruturas “espalhadas”

A seguir, as técnicas mais efetivas.

### Limite de bones por draw (Bone remap por submesh)

Se cada submesh usa 50 bones em vez de 200:

- você calcula menos
- sobe menos dados
- desenha mais barato

É uma otimização grande e direta.

### Use Transform (T/R/S) como base e gere matriz só no final

Em vez de carregar matrizes “cedo”, mantenha pose como:

- Vector3 T
- Quaternion R
- Vector3 S

E só converta para matriz quando precisar montar skin[i].

Isso reduz custo quando você faz:

- blending
- masks
- layers

…porque blend em quat/vec é mais barato e cache-friendly.

### Cache-friendly layout (SoA) para pose

Evite “array de structs” pesado.

Prefira Structure of Arrays (SoA):

- posX[] posY[] posZ[]
- rotX[] rotY[] rotZ[] rotW[]
- sclX[] sclY[] sclZ[]

Ou um meio-termo (AoS leve) como:

- Vector3[] pos; Quaternion[] rot; Vector3[] scl;

O importante: arrays contíguos por canal.

### SIMD (Single Instruction Multiple Data) quando fizer sentido

Usar System.Numerics.Vector<T>/Vector3/Quaternion e evitar alocações.
Mesmo sem “intrinsics explícitos”, manter arrays contíguos ajuda o JIT.

### Trocar “palette de matrizes” por texture palette (se necessário)

Se você bater no limite de constant buffer/uniforms, ou upload virar o custo dominante:

- empacote skin matrices em uma Texture2D (RGBA32F) e amostre no vertex shader
- cada bone vira 3 ou 4 texels (linhas da matriz)

Isso reduz pressão de uniforms e pode ajudar no batching (depende da plataforma).

5.6 Dual Quaternion Skinning (DQ) para menos artefato e (às vezes) menos custo

DQS (Dual Quaternion Skinning) reduz “candy wrapper effect” e pode ser mais compacto que 3x4 matrizes.
No shader, você mistura dual quats e aplica ao vértice.

Mas:

- implementação é mais complexa
- você ainda precisa de “global pose” de alguma forma

Minha opinião: comece com matriz 3x4, evolua para DQS se artefato virar problema.

5.7 Animation LOD (Level of Detail)

Para personagens distantes:

- update animação a cada N frames
- ou use clip mais simples
- ou reduza bones (rig “LOD skeleton”)

Isso costuma ser a diferença entre “ok” e “quebrou” em cenas grandes.

## GPU Skinning no MonoGame

### Vertex layout para skinning (4 influências)

No Vertex:

- BlendIndices (Byte4 ou UShort4)
- BlendWeights (NormalizedByte4 ou Vector4)

No shader (conceito):

- recupera os 4 bones
- combina: skinnedPos = Σ weight[j] * (boneMatrix[idx[j]] * pos)
- idem normal/tangent (com matriz adequada)

### Upload da paleta

Por draw call (submesh):

- você sobe paleta do submesh (com remap)

Cuidado com:

- limitar nº de bones por submesh
- evitar alocar arrays a cada frame (reuse buffers)

### Batching por “mesma paleta” é raro

Como cada personagem tem pose diferente, batching amplo costuma ser limitado. O que dá para fazer:

- reduzir draw calls com instancing somente se você conseguir compartilhar animação (crowds).


## Roteiro de implementação (passo a passo)

1. Defina seu runtime asset
  - Skeleton + Mesh + Clips + (opcional) bone remap
2. Faça um loader (ContentManager ou arquivo próprio)
3. Crie o Animator
  - playback de 1 clip
  - sample de T/R/S
4. Implemente blending
  - crossfade entre clips
5. Implemente máscara
  - layer upper-body
6. Gere model pose
  - loop por parentIndex
7. Gere skin palette
  - skin = model * invBind
  - aplique bone remap por submesh
8. Shader GPU skinning
  - matrizes no cbuffer (ou texture)
9. Otimize
  - SoA/arrays contíguos
  - reuse buffers
  - LOD
  - remap

⸻

Comece com:

- Matrix palette 3x4 (mais simples) + bone remap por submesh (otimização de maior retorno).
- Pose em Vector3/Quaternion/Vector3 (melhor para blend e cache).
- Só depois (se necessário):
- texture palette
- dual quaternion
- update LOD

Isso costuma entregar o melhor custo/benefício e evita “overengineering” cedo.
