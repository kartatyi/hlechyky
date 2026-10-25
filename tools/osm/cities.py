"""Міста для імпорту: bbox (пд, зх, пн, сх у градусах), центр проєкції (0, 0), тайли DEM і що вивести в мапу гри
(місця — за id об'єкта OSM з проміжного файлу, pois[].osm; вантажі — у форматі kray.json)."""

CITIES = {
    'zhytomyr': {
        'title': 'Житомир',
        # місто з окружною й виїздами на Київ, Новоград, Бердичів і Коростень (≈ 17 × 14,5 км)
        'bbox': (50.195, 28.545, 50.325, 28.785),
        # Майдан Корольова — серце міста
        'center': (50.2540, 28.6600),
        # тайл Copernicus GLO-30: південно-західний кут (lat, lon) у цілих градусах
        'dem_tiles': [(50, 28)],
        'river': 'Тетерів',
        'river_width': 50,
        'places': [
            {'id': 'dvir', 'osm': 'way/985998976', 'title': 'Автобаза АТП-11827', 'short': 'Автобаза', 'depot': True,
             'docks': 4},
            {'id': 'vokzal', 'osm': 'way/261284321', 'title': 'Вантажний двір вокзалу Житомир', 'short': 'Вокзал'},
            {'id': 'rynok', 'osm': 'way/89181486', 'title': 'Житній ринок', 'short': 'Житній ринок'},
            {'id': 'metro', 'osm': 'way/348915283', 'title': 'Склад «МЕТРО»', 'short': 'МЕТРО'},
            {'id': 'epicentr', 'osm': 'way/1009294510', 'title': 'Епіцентр К', 'short': 'Епіцентр'},
            {'id': 'rud', 'osm': 'way/376788911', 'title': 'Маслозавод «Рудь»', 'short': 'Рудь'},
            {'id': 'myaso', 'osm': 'way/270051292', 'title': "М'ясокомбінат (Східний промвузол)", 'short': "М'ясокомбінат"},
            {'id': 'silikat', 'osm': 'way/270050666', 'title': 'Комбінат силікатних виробів', 'short': 'Силікатний'},
            {'id': 'karton', 'osm': 'relation/15569469', 'title': 'Картонний комбінат', 'short': 'Картонний'},
            {'id': 'ferplast', 'osm': 'relation/15584063', 'title': 'Завод «Ферпласт»', 'short': 'Ферпласт'},
        ],
        'cargo': [
            {'id': 'icecream', 'title': 'Морозиво «Рудь»', 'emoji': '🍦', 'mult': 1.5, 'fragile': 0.6, 'mass': 9000,
             'from': ['rud'], 'to': ['metro', 'rynok', 'vokzal', 'epicentr']},
            {'id': 'butter', 'title': 'Масло й сир', 'emoji': '🧈', 'mult': 1.3, 'fragile': 0.4, 'mass': 10000,
             'from': ['rud'], 'to': ['metro', 'rynok', 'dvir']},
            {'id': 'meat', 'title': "М'ясо й ковбаси", 'emoji': '🥩', 'mult': 1.4, 'fragile': 0.5, 'mass': 11000,
             'from': ['myaso'], 'to': ['metro', 'rynok', 'vokzal']},
            {'id': 'brick', 'title': 'Силікатна цегла', 'emoji': '🧱', 'mult': 1.2, 'fragile': 1.2, 'mass': 20000,
             'from': ['silikat'], 'to': ['epicentr', 'vokzal', 'dvir', 'ferplast']},
            {'id': 'carton', 'title': 'Картон і тара', 'emoji': '📦', 'mult': 1.0, 'fragile': 0, 'mass': 7000,
             'from': ['karton'], 'to': ['rud', 'myaso', 'ferplast', 'metro', 'epicentr']},
            {'id': 'plastic', 'title': 'Пластикові вироби', 'emoji': '🪣', 'mult': 1.1, 'fragile': 0.2, 'mass': 6000,
             'from': ['ferplast'], 'to': ['metro', 'epicentr', 'vokzal', 'rynok']},
            {'id': 'containers', 'title': 'Контейнер із поїзда', 'emoji': '🚂', 'mult': 1.2, 'fragile': 0.3, 'mass': 18000,
             'from': ['vokzal'], 'to': ['metro', 'epicentr', 'karton', 'myaso', 'silikat']},
            {'id': 'goods', 'title': 'Товари для крамниць', 'emoji': '🛒', 'mult': 1.1, 'fragile': 0.3, 'mass': 8000,
             'from': ['metro', 'epicentr'], 'to': ['rynok', 'dvir', 'vokzal']},
            {'id': 'veg', 'title': 'Овочі з ринку', 'emoji': '🥔', 'mult': 1.0, 'fragile': 0.1, 'mass': 12000,
             'from': ['rynok'], 'to': ['metro', 'dvir', 'rud']},
        ],
    },
}
