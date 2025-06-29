const loader = new THREE.STLLoader();
            loader.load('model.stl', function(geometry) {
                const mesh = new THREE.Mesh(geometry, new THREE.MeshNormalMaterial());
                scene.add(mesh);