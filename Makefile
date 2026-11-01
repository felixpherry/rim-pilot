.PHONY: check test build build-game package verify-package

# Asset-free defaults. Never infer or launch an installed game.
check:
	python3 scripts/project.py check

test:
	python3 scripts/project.py test

build:
	python3 scripts/project.py build

# Explicit proprietary references; local compilation, not runtime acceptance.
build-game:
	python3 scripts/project.py build-game

# Complete development distributable; requires the same explicit reference roots.
package:
	python3 scripts/project.py package

verify-package:
	python3 scripts/project.py verify-package
