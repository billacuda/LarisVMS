namespace LarisVMS.Vision.Inference;

/// <summary>
/// D-FINE's own id2label tables, straight from each variant's Hugging Face config.json (see the
/// D-FINE integration design's Phase 0 verification — these were pulled directly from
/// ustc-community/dfine-small-obj2coco and ustc-community/dfine-small-obj365, not reconstructed
/// from memory, since even one wrong index would silently mislabel every detection of that class).
///
/// Obj2Coco keeps COCO's own legacy PASCAL-VOC-era spellings ("motorbike" not "motorcycle",
/// "aeroplane" not "airplane", "sofa"/"pottedplant"/"diningtable"/"tvmonitor") — see
/// CocoCategoryMap's own doc comment for why this matters (those spellings weren't in its
/// Vehicle/Animal lookup before this integration, so a motorbike/aeroplane detection silently fell
/// through to the "Object" catch-all).
/// </summary>
public static class DFineLabels
{
    /// <summary>D-FINE's obj2coco/coco variants: 80 classes, index-matched to the model's own
    /// `logits` output (index 0 = "person", ..., index 79 = "toothbrush").</summary>
    public static readonly IReadOnlyList<string> Obj2Coco =
    [
        "person", "bicycle", "car", "motorbike", "aeroplane", "bus", "train", "truck", "boat",
        "traffic light", "fire hydrant", "stop sign", "parking meter", "bench", "bird", "cat",
        "dog", "horse", "sheep", "cow", "elephant", "bear", "zebra", "giraffe", "backpack",
        "umbrella", "handbag", "tie", "suitcase", "frisbee", "skis", "snowboard", "sports ball",
        "kite", "baseball bat", "baseball glove", "skateboard", "surfboard", "tennis racket",
        "bottle", "wine glass", "cup", "fork", "knife", "spoon", "bowl", "banana", "apple",
        "sandwich", "orange", "broccoli", "carrot", "hot dog", "pizza", "donut", "cake", "chair",
        "sofa", "pottedplant", "bed", "diningtable", "toilet", "tvmonitor", "laptop", "mouse",
        "remote", "keyboard", "cell phone", "microwave", "oven", "toaster", "sink", "refrigerator",
        "book", "clock", "vase", "scissors", "teddy bear", "hair drier", "toothbrush",
    ];

    /// <summary>D-FINE's obj365 variant: 366 entries, index-matched to the model's own `logits`
    /// output. Index 0 is "None" — a padding/background slot, never a real detected class; the
    /// decoder must filter it out rather than mapping it to a category.</summary>
    public static readonly IReadOnlyList<string> Obj365 =
    [
        "None", "Person", "Sneakers", "Chair", "Other Shoes", "Hat", "Car", "Lamp", "Glasses",
        "Bottle", "Desk", "Cup", "Street Lights", "Cabinet/shelf", "Handbag/Satchel", "Bracelet",
        "Plate", "Picture/Frame", "Helmet", "Book", "Gloves", "Storage box", "Boat",
        "Leather Shoes", "Flower", "Bench", "Potted Plant", "Bowl/Basin", "Flag", "Pillow",
        "Boots", "Vase", "Microphone", "Necklace", "Ring", "SUV", "Wine Glass", "Belt",
        "Monitor/TV", "Backpack", "Umbrella", "Traffic Light", "Speaker", "Watch", "Tie",
        "Trash bin Can", "Slippers", "Bicycle", "Stool", "Barrel/bucket", "Van", "Couch",
        "Sandals", "Basket", "Drum", "Pen/Pencil", "Bus", "Wild Bird", "High Heels", "Motorcycle",
        "Guitar", "Carpet", "Cell Phone", "Bread", "Camera", "Canned", "Truck", "Traffic cone",
        "Cymbal", "Lifesaver", "Towel", "Stuffed Toy", "Candle", "Sailboat", "Laptop", "Awning",
        "Bed", "Faucet", "Tent", "Horse", "Mirror", "Power outlet", "Sink", "Apple",
        "Air Conditioner", "Knife", "Hockey Stick", "Paddle", "Pickup Truck", "Fork",
        "Traffic Sign", "Balloon", "Tripod", "Dog", "Spoon", "Clock", "Pot", "Cow", "Cake",
        "Dinning Table", "Sheep", "Hanger", "Blackboard/Whiteboard", "Napkin", "Other Fish",
        "Orange/Tangerine", "Toiletry", "Keyboard", "Tomato", "Lantern", "Machinery Vehicle",
        "Fan", "Green Vegetables", "Banana", "Baseball Glove", "Airplane", "Mouse", "Train",
        "Pumpkin", "Soccer", "Skiboard", "Luggage", "Nightstand", "Tea pot", "Telephone",
        "Trolley", "Head Phone", "Sports Car", "Stop Sign", "Dessert", "Scooter", "Stroller",
        "Crane", "Remote", "Refrigerator", "Oven", "Lemon", "Duck", "Baseball Bat",
        "Surveillance Camera", "Cat", "Jug", "Broccoli", "Piano", "Pizza", "Elephant",
        "Skateboard", "Surfboard", "Gun", "Skating and Skiing shoes", "Gas stove", "Donut",
        "Bow Tie", "Carrot", "Toilet", "Kite", "Strawberry", "Other Balls", "Shovel", "Pepper",
        "Computer Box", "Toilet Paper", "Cleaning Products", "Chopsticks", "Microwave", "Pigeon",
        "Baseball", "Cutting/chopping Board", "Coffee Table", "Side Table", "Scissors", "Marker",
        "Pie", "Ladder", "Snowboard", "Cookies", "Radiator", "Fire Hydrant", "Basketball", "Zebra",
        "Grape", "Giraffe", "Potato", "Sausage", "Tricycle", "Violin", "Egg", "Fire Extinguisher",
        "Candy", "Fire Truck", "Billiards", "Converter", "Bathtub", "Wheelchair", "Golf Club",
        "Briefcase", "Cucumber", "Cigar/Cigarette", "Paint Brush", "Pear", "Heavy Truck",
        "Hamburger", "Extractor", "Extension Cord", "Tong", "Tennis Racket", "Folder",
        "American Football", "earphone", "Mask", "Kettle", "Tennis", "Ship", "Swing",
        "Coffee Machine", "Slide", "Carriage", "Onion", "Green beans", "Projector", "Frisbee",
        "Washing Machine/Drying Machine", "Chicken", "Printer", "Watermelon", "Saxophone",
        "Tissue", "Toothbrush", "Ice cream", "Hot-air balloon", "Cello", "French Fries", "Scale",
        "Trophy", "Cabbage", "Hot dog", "Blender", "Peach", "Rice", "Wallet/Purse", "Volleyball",
        "Deer", "Goose", "Tape", "Tablet", "Cosmetics", "Trumpet", "Pineapple", "Golf Ball",
        "Ambulance", "Parking meter", "Mango", "Key", "Hurdle", "Fishing Rod", "Medal", "Flute",
        "Brush", "Penguin", "Megaphone", "Corn", "Lettuce", "Garlic", "Swan", "Helicopter",
        "Green Onion", "Sandwich", "Nuts", "Speed Limit Sign", "Induction Cooker", "Broom",
        "Trombone", "Plum", "Rickshaw", "Goldfish", "Kiwi fruit", "Router/modem", "Poker Card",
        "Toaster", "Shrimp", "Sushi", "Cheese", "Notepaper", "Cherry", "Pliers", "CD", "Pasta",
        "Hammer", "Cue", "Avocado", "Hamimelon", "Flask", "Mushroom", "Screwdriver", "Soap",
        "Recorder", "Bear", "Eggplant", "Board Eraser", "Coconut", "Tape Measure/Ruler", "Pig",
        "Showerhead", "Globe", "Chips", "Steak", "Crosswalk Sign", "Stapler", "Camel",
        "Formula 1", "Pomegranate", "Dishwasher", "Crab", "Hoverboard", "Meat ball",
        "Rice Cooker", "Tuba", "Calculator", "Papaya", "Antelope", "Parrot", "Seal", "Butterfly",
        "Dumbbell", "Donkey", "Lion", "Urinal", "Dolphin", "Electric Drill", "Hair Dryer",
        "Egg tart", "Jellyfish", "Treadmill", "Lighter", "Grapefruit", "Game board", "Mop",
        "Radish", "Baozi", "Target", "French", "Spring Rolls", "Monkey", "Rabbit", "Pencil Case",
        "Yak", "Red Cabbage", "Binoculars", "Asparagus", "Barbell", "Scallop", "Noddles", "Comb",
        "Dumpling", "Oyster", "Table Tennis paddle", "Cosmetics Brush/Eyeliner Pencil", "Chainsaw",
        "Eraser", "Lobster", "Durian", "Okra", "Lipstick", "Cosmetics Mirror", "Curling",
        "Table Tennis",
    ];
}
