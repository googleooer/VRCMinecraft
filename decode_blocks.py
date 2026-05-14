import re

with open('D:/vrchat_projects/VRCMinecraft/Assets/VRCMinecraft/scenes/Minecraft.unity', 'r') as f:
    content = f.read()

def get_arr(name):
    return re.search(name + r': ([0-9a-f]+)', content).group(1)

def parse_byte_array(hex_str, n=97):
    return [int(hex_str[i*2:i*2+2], 16) for i in range(n)]

def parse_int_array(hex_str, n=97):
    res = []
    for i in range(n):
        b = hex_str[i*8:i*8+8]
        val = int(b[6:8] + b[4:6] + b[2:4] + b[0:2], 16)
        res.append(val)
    return res

isSolid = parse_byte_array(get_arr('isSolidData'))
vis = parse_int_array(get_arr('blockVisibilityTypeData'))
cull = parse_int_array(get_arr('blockCullingTypeData'))
shape = parse_int_array(get_arr('blockShapeTypeData'))
mapping = parse_int_array(get_arr('textureMappingTypeData'))
lightOp = parse_int_array(get_arr('lightOpacityData'))
lightEm = parse_int_array(get_arr('lightEmissionData'))
canBlockGrass = parse_byte_array(get_arr('canBlockGrassData'))
uv_all = parse_int_array(get_arr('uv_allFacesData'))
uv_top = parse_int_array(get_arr('uv_topFaceData'))
uv_bot = parse_int_array(get_arr('uv_bottomFaceData'))
uv_side = parse_int_array(get_arr('uv_sideFacesData'))

names = ['Air','Stone','Grass','Dirt','Cobblestone','Planks','Oak_Sapling','Bedrock','Water_Flowing','Water_Still','Lava_Flowing','Lava_Still','Sand','Gravel','Gold_Ore','Iron_Ore','Coal_Ore','Log','Leaves','Sponge','Glass','Lapis_Lazuli_Ore','Lapis_Lazuli_Block','Dispenser','Sandstone','Note_Block','Bed','Powered_Rail','Detector_Rail','Sticky_Piston','Cobweb','Tall_Grass','Dead_Bush','Piston','Piston_Head','Wool','Moving_Piston','Dandelion','Rose','Brown_Mushroom','Red_Mushroom','Gold_Block','Iron_Block','Double_Slab','Slab','Bricks','TNT','Bookshelf','Mossy_Cobblestone','Obsidian','Torch','Fire','Mob_Spawner','Wooden_Stairs','Chest','Redstone_Wire','Diamond_Ore','Diamond_Block','Crafting_Table','Crops','Farmland','Furnace','Furnace_Lit','Sign_Post','Wooden_Door','Ladder','Rail','Cobblestone_Stairs','Wall_Sign','Lever','Stone_Pressure_Plate','Iron_Door','Wooden_Pressure_Plate','Redstone_Ore','Redstone_Ore_Lit','Redstone_Torch_Off','Redstone_Torch_On','Stone_Button','Snow_Layer','Ice','Snow_Block','Cactus','Clay','Reeds','Jukebox','Fence','Pumpkin','Netherrack','Soul_Sand','Glowstone','Portal','Jack_O_Lantern','Cake','Repeater_Off','Repeater_On','Locked_Chest','Trapdoor']

vis_names = ['Opaque','Transparent','Cutout','Invisible']
cull_names = ['NoCull','CullSelf','CullSelfAndOpaque','CullSelfAndCutout','CullSelfAndTransparent','CullAll']
shape_names = ['Cube','Cross']
map_names = ['AllFacesSame','TopBottomSides']

print(f"{'ID':3} {'Name':22} {'Sol':3} {'Vis':12} {'Cull':22} {'Shp':5} {'Map':15} {'Op':3} {'Em':3} {'CBG':3} {'allF':>5} {'top':>5} {'bot':>5} {'side':>5}")
for i in range(97):
    vs = vis_names[vis[i]] if vis[i] < len(vis_names) else f"V?{vis[i]}"
    cs = cull_names[cull[i]] if cull[i] < len(cull_names) else f"C?{cull[i]}"
    ss = shape_names[shape[i]] if shape[i] < len(shape_names) else f"S?{shape[i]}"
    ms = map_names[mapping[i]] if mapping[i] < len(map_names) else f"M?{mapping[i]}"
    print(f"{i:3} {names[i]:22} {isSolid[i]:3} {vs:12} {cs:22} {ss:5} {ms:15} {lightOp[i]:3} {lightEm[i]:3} {canBlockGrass[i]:3} {uv_all[i]:5} {uv_top[i]:5} {uv_bot[i]:5} {uv_side[i]:5}")
